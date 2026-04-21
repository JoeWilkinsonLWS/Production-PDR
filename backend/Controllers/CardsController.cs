using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace HeijunkaWeb.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class CardsController : ControllerBase
{
    private readonly SqlConnection _sql;

    public CardsController(SqlConnection sql) => _sql = sql;

    // GET api/cards/{cardno}
    [HttpGet("{cardno}")]
    public async Task<IActionResult> GetCard(string cardno)
    {
        const string sql = @"
            SELECT cardno, order_status, notes, So_id, so_line,
                   factory_date, cust_name, country,
                   desc1 AS part_id, desc2 AS part_desc,
                   loaded_date, assembled_date, tested_date, backflush_date,
                   card_status
            FROM dbo.Heijunka_Card
            WHERE cardno = @cardno";

        await _sql.OpenAsync();
        await using var cmd = new SqlCommand(sql, _sql);
        cmd.Parameters.AddWithValue("@cardno", cardno);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return NotFound(new { message = $"Card {cardno} not found." });

        var card = new
        {
            cardno        = reader["cardno"].ToString(),
            orderStatus   = reader["order_status"].ToString()?.Trim(),
            notes         = reader["notes"] == DBNull.Value ? "" : reader["notes"].ToString()?.Trim(),
            soId          = reader["So_id"] == DBNull.Value ? "" : reader["So_id"].ToString()?.Trim(),
            soLine        = reader["so_line"] == DBNull.Value ? (decimal?)null : (decimal)reader["so_line"],
            factoryDate   = reader["factory_date"] == DBNull.Value ? (DateTime?)null : (DateTime)reader["factory_date"],
            custName      = reader["cust_name"] == DBNull.Value ? "" : reader["cust_name"].ToString()?.Trim(),
            country       = reader["country"] == DBNull.Value ? "" : reader["country"].ToString()?.Trim(),
            partId        = reader["part_id"] == DBNull.Value ? "" : reader["part_id"].ToString()?.Trim(),
            partDesc      = reader["part_desc"] == DBNull.Value ? "" : reader["part_desc"].ToString()?.Trim(),
            loadedDate    = reader["loaded_date"] == DBNull.Value ? (DateTime?)null : (DateTime)reader["loaded_date"],
            assembledDate = reader["assembled_date"] == DBNull.Value ? (DateTime?)null : (DateTime)reader["assembled_date"],
            testedDate    = reader["tested_date"] == DBNull.Value ? (DateTime?)null : (DateTime)reader["tested_date"],
            backflushDate = reader["backflush_date"] == DBNull.Value ? (DateTime?)null : (DateTime)reader["backflush_date"],
            cardStatus    = reader["card_status"].ToString()?.Trim()
        };

        return Ok(card);
    }

    // PUT api/cards/{cardno}/status
    [HttpPut("{cardno}/status")]
    public async Task<IActionResult> UpdateStatus(string cardno, [FromBody] UpdateStatusRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Status))
            return BadRequest(new { message = "Status is required." });

        var validStatuses = new[] { "None", "Printed", "Loaded", "Assembly Complete", "Test Complete", "Backflush Complete" };
        if (!validStatuses.Contains(req.Status))
            return BadRequest(new { message = "Invalid status value." });

        string sql = req.Status switch
        {
            "None"                => "UPDATE dbo.Heijunka_Card SET order_status='None', notes=@notes WHERE cardno=@cardno",
            "Printed"             => "UPDATE dbo.Heijunka_Card SET order_status='Printed', notes=@notes WHERE cardno=@cardno",
            "Loaded"              => "UPDATE dbo.Heijunka_Card SET order_status='Loaded', loaded_date=GETDATE(), notes=@notes WHERE cardno=@cardno",
            "Assembly Complete"   => "UPDATE dbo.Heijunka_Card SET order_status='Assembly Complete', assembled_date=GETDATE(), notes=@notes WHERE cardno=@cardno",
            "Test Complete"       => "UPDATE dbo.Heijunka_Card SET order_status='Test Complete', tested_date=GETDATE(), notes=@notes WHERE cardno=@cardno",
            "Backflush Complete"  => "UPDATE dbo.Heijunka_Card SET card_status='Inactive', order_status='Backflush Complete', backflush_date=GETDATE(), notes=@notes WHERE cardno=@cardno",
            _                     => throw new InvalidOperationException()
        };

        await _sql.OpenAsync();
        await using var cmd = new SqlCommand(sql, _sql);
        cmd.Parameters.AddWithValue("@cardno", cardno);
        cmd.Parameters.AddWithValue("@notes", (object?)req.Notes ?? DBNull.Value);

        int rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0) return NotFound(new { message = $"Card {cardno} not found." });

        return Ok(new { message = "Card updated successfully." });
    }

    // GET api/cards/open-orders
    [HttpGet("open-orders")]
    public async Task<IActionResult> GetOpenOrders()
    {
        const string sql = @"
            SELECT SO_ID, SO_LINE_NO, CUSTOMER_NAME, PART_ID, SO_DESC,
                   REV_ORDER_QTY, PART_TYPE, Factory_Date, country,
                   producingcell, Order_Status, Line_status
            FROM dbo.Heijunka_query
            ORDER BY Factory_Date";

        await _sql.OpenAsync();
        await using var cmd = new SqlCommand(sql, _sql);
        await using var reader = await cmd.ExecuteReaderAsync();

        var orders = new List<object>();
        while (await reader.ReadAsync())
        {
            orders.Add(new
            {
                soId          = reader["SO_ID"].ToString()?.Trim(),
                soLineNo      = (decimal)reader["SO_LINE_NO"],
                customerName  = reader["CUSTOMER_NAME"].ToString()?.Trim(),
                partId        = reader["PART_ID"].ToString()?.Trim(),
                soDesc        = reader["SO_DESC"].ToString()?.Trim(),
                revOrderQty   = (decimal)reader["REV_ORDER_QTY"],
                partType      = reader["PART_TYPE"] == DBNull.Value ? "" : reader["PART_TYPE"].ToString()?.Trim(),
                factoryDate   = reader["Factory_Date"] == DBNull.Value ? (DateTime?)null : (DateTime)reader["Factory_Date"],
                country       = reader["country"] == DBNull.Value ? "" : reader["country"].ToString()?.Trim(),
                producingCell = reader["producingcell"] == DBNull.Value ? "" : reader["producingcell"].ToString()?.Trim(),
                orderStatus   = reader["Order_Status"].ToString()?.Trim(),
                lineStatus    = reader["Line_status"].ToString()?.Trim()
            });
        }

        return Ok(orders);
    }
}

public record UpdateStatusRequest(string Status, string? Notes);
