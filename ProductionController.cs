using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Npgsql;
using System.Security.Claims;

namespace HeijunkaWeb.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ProductionController : ControllerBase
{
    private readonly SqlConnection _sql;
    private readonly NpgsqlConnection _pg;
    private readonly IConfiguration _config;

    public ProductionController(SqlConnection sql, NpgsqlConnection pg, IConfiguration config)
    {
        _sql    = sql;
        _pg     = pg;
        _config = config;
    }

    // GET api/production/producing-cells
    // Returns all producing cells + lines + PDR targets from SQL Server
    [HttpGet("producing-cells")]
    public async Task<IActionResult> GetProducingCells()
    {
        const string sql = @"
            SELECT RECNUM, Producing_Cell, Prod_Line, PDR, PDR_Adder,
                   CycleTime_Minutes, DailyLoad_Minutes,
                   Blackout_Start, Blackout_End
            FROM dbo.L_LevelLoad_Factory
            ORDER BY Producing_Cell, Prod_Line";

        await _sql.OpenAsync();
        await using var cmd = new SqlCommand(sql, _sql);
        await using var reader = await cmd.ExecuteReaderAsync();

        var cells = new List<object>();
        while (await reader.ReadAsync())
        {
            cells.Add(new
            {
                recnum          = reader["RECNUM"].ToString()?.Trim(),
                producingCell   = reader["Producing_Cell"].ToString()?.Trim(),
                prodLine        = reader["Prod_Line"].ToString()?.Trim(),
                pdr             = (decimal)reader["PDR"],
                pdrAdder        = (decimal)reader["PDR_Adder"],
                cycleTimeMin    = (decimal)reader["CycleTime_Minutes"],
                dailyLoadMin    = (decimal)reader["DailyLoad_Minutes"],
                blackoutStart   = reader["Blackout_Start"] == DBNull.Value ? (DateTime?)null : (DateTime)reader["Blackout_Start"],
                blackoutEnd     = reader["Blackout_End"]   == DBNull.Value ? (DateTime?)null : (DateTime)reader["Blackout_End"]
            });
        }

        return Ok(cells);
    }

    // GET api/production/session?producingCell=X&prodLine=Y
    // Gets or creates today's session for the logged-in operator
    [HttpGet("session")]
    public async Task<IActionResult> GetSession([FromQuery] string producingCell, [FromQuery] string prodLine)
    {
        var (name, email) = GetOperatorInfo();

        await _pg.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, entry_date, producing_cell, prod_line, operator_name,
                   pdr_target, units_completed, met_pdr, miss_reason_id,
                   miss_notes, session_open, created_at
            FROM daily_production
            WHERE entry_date = CURRENT_DATE
              AND producing_cell = @cell
              AND prod_line = @line
              AND operator_email = @email", _pg);

        cmd.Parameters.AddWithValue("cell",  producingCell);
        cmd.Parameters.AddWithValue("line",  prodLine);
        cmd.Parameters.AddWithValue("email", email);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return Ok(null); // No session yet — frontend will offer to start one

        return Ok(new
        {
            id             = (int)reader["id"],
            entryDate      = ((DateTime)reader["entry_date"]).ToString("yyyy-MM-dd"),
            producingCell  = reader["producing_cell"].ToString(),
            prodLine       = reader["prod_line"].ToString(),
            operatorName   = reader["operator_name"].ToString(),
            pdrTarget      = (decimal)reader["pdr_target"],
            unitsCompleted = (int)reader["units_completed"],
            metPdr         = reader["met_pdr"] == DBNull.Value ? (bool?)null : (bool)reader["met_pdr"],
            missReasonId   = reader["miss_reason_id"] == DBNull.Value ? (int?)null : (int)reader["miss_reason_id"],
            missNotes      = reader["miss_notes"] == DBNull.Value ? "" : reader["miss_notes"].ToString(),
            sessionOpen    = (bool)reader["session_open"],
            createdAt      = (DateTime)reader["created_at"]
        });
    }

    // POST api/production/session
    // Start a new session for today
    [HttpPost("session")]
    public async Task<IActionResult> StartSession([FromBody] StartSessionRequest req)
    {
        var (name, email) = GetOperatorInfo();

        // Get PDR target from SQL Server
        await _sql.OpenAsync();
        await using var sqlCmd = new SqlCommand(
            "SELECT PDR FROM dbo.L_LevelLoad_Factory WHERE Producing_Cell=@cell AND Prod_Line=@line", _sql);
        sqlCmd.Parameters.AddWithValue("@cell", req.ProducingCell);
        sqlCmd.Parameters.AddWithValue("@line", req.ProdLine);
        var pdrObj = await sqlCmd.ExecuteScalarAsync();
        if (pdrObj == null) return BadRequest(new { message = "Producing cell/line not found." });
        decimal pdr = (decimal)pdrObj;

        await _pg.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO daily_production
                (entry_date, producing_cell, prod_line, operator_name, operator_email, pdr_target)
            VALUES (CURRENT_DATE, @cell, @line, @name, @email, @pdr)
            ON CONFLICT (entry_date, producing_cell, prod_line, operator_email) DO NOTHING
            RETURNING id, units_completed, pdr_target, session_open", _pg);

        cmd.Parameters.AddWithValue("cell",  req.ProducingCell);
        cmd.Parameters.AddWithValue("line",  req.ProdLine);
        cmd.Parameters.AddWithValue("name",  name);
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("pdr",   pdr);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return Conflict(new { message = "Session already exists for today." });

        return Ok(new
        {
            id             = (int)reader["id"],
            pdrTarget      = (decimal)reader["pdr_target"],
            unitsCompleted = (int)reader["units_completed"],
            sessionOpen    = (bool)reader["session_open"]
        });
    }

    // POST api/production/session/{id}/tap
    // Add one unit to the ticker
    [HttpPost("session/{id}/tap")]
    public async Task<IActionResult> Tap(int id)
    {
        await _pg.OpenAsync();

        // Verify session belongs to this operator and is open
        await using var check = new NpgsqlCommand(
            "SELECT operator_email, session_open FROM daily_production WHERE id=@id", _pg);
        check.Parameters.AddWithValue("id", id);
        await using var cr = await check.ExecuteReaderAsync();
        if (!await cr.ReadAsync()) return NotFound();
        var (_, email) = GetOperatorInfo();
        if (cr["operator_email"].ToString() != email) return Forbid();
        if (!(bool)cr["session_open"]) return BadRequest(new { message = "Session is closed." });
        await cr.CloseAsync();

        // Insert tap and increment counter atomically
        await using var cmd = new NpgsqlCommand(@"
            WITH tap AS (
                INSERT INTO unit_taps (daily_production_id) VALUES (@id)
            )
            UPDATE daily_production
               SET units_completed = units_completed + 1
            WHERE id = @id
            RETURNING units_completed, pdr_target", _pg);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();

        int units    = (int)reader["units_completed"];
        decimal pdr  = (decimal)reader["pdr_target"];

        return Ok(new
        {
            unitsCompleted = units,
            pdrTarget      = pdr,
            metPdr         = units >= (int)Math.Ceiling(pdr)
        });
    }

    // POST api/production/session/{id}/close
    // Close the session at end of day, optionally recording miss reason
    [HttpPost("session/{id}/close")]
    public async Task<IActionResult> CloseSession(int id, [FromBody] CloseSessionRequest req)
    {
        await _pg.OpenAsync();

        // Get session to check PDR
        await using var getCmd = new NpgsqlCommand(
            "SELECT units_completed, pdr_target, operator_email FROM daily_production WHERE id=@id", _pg);
        getCmd.Parameters.AddWithValue("id", id);
        await using var gr = await getCmd.ExecuteReaderAsync();
        if (!await gr.ReadAsync()) return NotFound();
        var (_, email) = GetOperatorInfo();
        if (gr["operator_email"].ToString() != email) return Forbid();
        int    units  = (int)gr["units_completed"];
        decimal pdr   = (decimal)gr["pdr_target"];
        await gr.CloseAsync();

        bool metPdr = units >= (int)Math.Ceiling(pdr);

        // If missed PDR, miss reason is required
        if (!metPdr && req.MissReasonId == null)
            return BadRequest(new { message = "A miss reason is required when PDR was not met." });

        await using var cmd = new NpgsqlCommand(@"
            UPDATE daily_production
               SET session_open   = FALSE,
                   met_pdr        = @metPdr,
                   miss_reason_id = @missReason,
                   miss_notes     = @missNotes,
                   closed_at      = NOW()
            WHERE id = @id", _pg);

        cmd.Parameters.AddWithValue("id",         id);
        cmd.Parameters.AddWithValue("metPdr",      metPdr);
        cmd.Parameters.AddWithValue("missReason",  (object?)req.MissReasonId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("missNotes",   (object?)req.MissNotes    ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
        return Ok(new { metPdr, unitsCompleted = units });
    }

    // GET api/production/miss-reasons
    [HttpGet("miss-reasons")]
    public async Task<IActionResult> GetMissReasons()
    {
        await _pg.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT id, reason FROM pdr_miss_reasons WHERE active=TRUE ORDER BY sort_order", _pg);
        await using var reader = await cmd.ExecuteReaderAsync();

        var reasons = new List<object>();
        while (await reader.ReadAsync())
            reasons.Add(new { id = (int)reader["id"], reason = reader["reason"].ToString() });

        return Ok(reasons);
    }

    // GET api/production/shift-status
    // Returns shift timing info so the frontend can fire the 2PM warning
    [HttpGet("shift-status")]
    public IActionResult GetShiftStatus()
    {
        var tz       = TimeZoneInfo.FindSystemTimeZoneById(_config["ShiftSettings:Timezone"] ?? "America/Los_Angeles");
        var now      = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var endTime  = TimeOnly.Parse(_config["ShiftSettings:EndTimeLocal"] ?? "15:00");
        var warnMins = int.Parse(_config["ShiftSettings:WarningMinutesBefore"] ?? "60");

        var shiftEnd    = now.Date.Add(endTime.ToTimeSpan());
        var warnAt      = shiftEnd.AddMinutes(-warnMins);
        var minutesLeft = (shiftEnd - now).TotalMinutes;

        return Ok(new
        {
            nowLocal        = now.ToString("HH:mm"),
            shiftEnd        = shiftEnd.ToString("HH:mm"),
            warningAt       = warnAt.ToString("HH:mm"),
            minutesLeft     = (int)minutesLeft,
            inWarningWindow = now >= warnAt && now < shiftEnd,
            shiftOver       = now >= shiftEnd
        });
    }

    private (string name, string email) GetOperatorInfo()
    {
        var name  = User.FindFirst("name")?.Value
                 ?? User.FindFirst(ClaimTypes.Name)?.Value
                 ?? "Unknown";
        var email = User.FindFirst("preferred_username")?.Value
                 ?? User.FindFirst(ClaimTypes.Email)?.Value
                 ?? "unknown@unknown.com";
        return (name, email);
    }
}

public record StartSessionRequest(string ProducingCell, string ProdLine);
public record CloseSessionRequest(int? MissReasonId, string? MissNotes);
