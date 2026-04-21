# Heijunka Web App

A web-based production tracking and scheduling system built on Railway, connecting to your existing SQL Server (ESIDB) for card data while storing daily production entries in Railway's PostgreSQL.

---

## Architecture

```
Browser (index.html)
    │
    ├── Azure AD login (MSAL.js)
    │
    └── ASP.NET Core API (Railway service)
            │
            ├── SQL Server (your existing ESIDB)
            │       ├── Heijunka_Card       — card lookup & status updates
            │       ├── Heijunka_query      — open orders
            │       ├── xxProducingCell     — cell list
            │       └── L_LevelLoad_Factory — PDR targets
            │
            └── PostgreSQL (Railway service)
                    ├── daily_production    — operator sessions
                    ├── unit_taps           — individual +1 taps
                    ├── pdr_miss_reasons    — reasons for missing PDR
                    └── v_pdr_summary       — view for Domo dashboard
```

---

## Step 1 — Register the app in Azure AD

You (or your IT admin) need to do this once in the Azure portal.

1. Go to https://portal.azure.com → **Azure Active Directory** → **App registrations** → **New registration**
2. Name: `Heijunka Web App`
3. Supported account types: **Accounts in this organizational directory only**
4. Redirect URI: Select **Single-page application (SPA)** → enter `https://your-frontend.railway.app` (update after deploying)
5. Click **Register**
6. Copy the **Application (client) ID** → this is `YOUR_CLIENT_ID`
7. Copy the **Directory (tenant) ID** → this is `YOUR_TENANT_ID`
8. Go to **Expose an API** → **Add a scope**
   - Application ID URI: accept the default (`api://YOUR_CLIENT_ID`)
   - Scope name: `access_as_user`
   - Who can consent: **Admins and users**
   - Click **Add scope**
9. Go to **API permissions** → **Add a permission** → **My APIs** → select your app → check `access_as_user` → **Add**

---

## Step 2 — Set up Railway

### 2a. Create the PostgreSQL database
1. In your Railway project, click **+ New** → **Database** → **PostgreSQL**
2. Once provisioned, click the database → **Connect** tab → copy the connection string
3. Open a query tool (Railway has one built in under the **Data** tab) and run the contents of `sql/railway_schema.sql`

### 2b. Deploy the backend
1. Push the `backend/` folder to a GitHub repo (or create a new one)
2. In Railway: **+ New** → **GitHub Repo** → select it
3. Railway will detect the `.csproj` and build automatically
4. Go to **Variables** and add:
   ```
   ConnectionStrings__SqlServer=Server=YOUR_SQL_SERVER;Database=ESIDB;User Id=Heijunka;Password=YOUR_PASSWORD;TrustServerCertificate=True;
   ConnectionStrings__PostgreSQL=<your Railway PostgreSQL connection string>
   AzureAd__TenantId=YOUR_TENANT_ID
   AzureAd__ClientId=YOUR_CLIENT_ID
   AllowedOrigins=https://your-frontend.railway.app
   ShiftSettings__EndTimeLocal=15:00
   ShiftSettings__WarningMinutesBefore=60
   ShiftSettings__Timezone=America/Los_Angeles
   ```
5. Copy the generated backend URL (e.g. `https://heijunka-api.railway.app`)

### 2c. Deploy the frontend
1. In `frontend/index.html`, update the CONFIG block at the top:
   ```js
   const CONFIG = {
     clientId: 'YOUR_CLIENT_ID',       // from Step 1
     tenantId: 'YOUR_TENANT_ID',       // from Step 1
     apiBase:  'https://heijunka-api.railway.app/api',  // from Step 2b
     apiScope: 'api://YOUR_CLIENT_ID/access_as_user'
   };
   ```
2. Push the `frontend/` folder to a separate GitHub repo
3. In Railway: **+ New** → **GitHub Repo** → select it
4. Railway will serve the static HTML directly (no build step needed)
5. Copy the frontend URL and update:
   - The Railway backend `AllowedOrigins` variable
   - The Azure AD redirect URI (Step 1, item 4)

---

## Step 3 — Make your SQL Server reachable

Your existing SQL Server needs to accept connections from Railway's IP ranges.

**Option A — Direct connection (simplest):**
- Open your SQL Server firewall to allow inbound connections on port 1433 from Railway's egress IPs
- Railway's current egress IPs are listed at: https://docs.railway.com/reference/static-outbound-ips

**Option B — VPN/tunnel:**
- If your SQL Server is strictly internal, set up a site-to-site VPN or use a tool like Tailscale to bridge Railway to your network

---

## Step 4 — Connect Domo to PostgreSQL

1. In Domo, go to **Connectors** → search **PostgreSQL**
2. Use the Railway PostgreSQL connection details (host, port, database, username, password)
3. Connect to the `v_pdr_summary` view for the dashboard dataset
4. Build a calendar visualization using `entry_date` as the date field and `met_pdr` for color coding

---

## Adding PDR Miss Reasons Later

Connect to the Railway PostgreSQL database and run:
```sql
INSERT INTO pdr_miss_reasons (reason, sort_order) VALUES ('Your New Reason', 4);
```

---

## File Structure

```
heijunka-web/
├── backend/
│   ├── HeijunkaWeb.csproj
│   ├── Program.cs
│   ├── appsettings.json          ← template, fill in values
│   └── Controllers/
│       ├── CardsController.cs    ← reads/writes SQL Server
│       └── ProductionController.cs ← reads SQL Server PDR, writes PostgreSQL
├── frontend/
│   └── index.html                ← single-file web app
└── sql/
    └── railway_schema.sql        ← run this in Railway PostgreSQL
```
