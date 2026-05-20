# Yool API (ASP.NET)

Backend for the Yool workspace app: JWT auth, Google OAuth, Neon/Postgres (JSONB), WaafiPay, SignalR collaboration, templates, and AI helpers.

## Local development

1. Copy environment file:
   ```bash
   cp .env.example .env
   ```
2. Fill in `.env` (Neon connection string, JWT secrets, email, Google, etc.).
3. Run:
   ```bash
   dotnet run
   ```
4. API: `http://localhost:5000` — health: `GET /health`

Point the React frontend `VITE_API_PROXY` (or production `VITE_API_URL`) at this URL.

## Push to GitHub

From this folder (`aspbackend/`):

```bash
git init
git add .
git commit -m "Initial commit: Yool ASP.NET API ready for deployment"
git branch -M main
git remote add origin https://github.com/YOUR_USERNAME/YOUR_REPO.git
git push -u origin main
```

**Do not commit `.env`** — it is listed in `.gitignore`.

## Deploy (Render — recommended)

1. Create a [Render](https://render.com) account.
2. **New → Web Service** → connect your GitHub repo (this folder as root, or set **Root Directory** to `aspbackend` if the repo is the whole monorepo).
3. **Runtime**: Docker (uses `Dockerfile` in this directory).
4. **Health check path**: `/health`
5. Add **Environment variables** (from `.env.example`). Minimum:

   | Variable | Example |
   |----------|---------|
   | `ASPNETCORE_ENVIRONMENT` | `Production` |
   | `CLIENT_URL` | `https://your-frontend.vercel.app` |
   | `API_PUBLIC_URL` | `https://yool-api.onrender.com` |
   | `JWT_SECRET` | long random string |
   | `JWT_REFRESH_SECRET` | long random string |
   | `ConnectionStrings__Neon` | Neon pooled connection string |
   | `GOOGLE_CALLBACK_URL` | `https://yool-api.onrender.com/api/auth/google/callback` |

6. In **Google Cloud Console**, add the production redirect URI and set `CLIENT_URL` to your deployed frontend origin for CORS.

Alternatively use **Render Blueprint**: connect repo and apply `render.yaml` (then fill secrets in the dashboard).

## Deploy (Docker anywhere)

```bash
docker build -t yool-api .
docker run -p 8080:8080 --env-file .env yool-api
```

## Other hosts

- **Railway / Fly.io / Azure App Service**: use the same `Dockerfile` or `dotnet publish` output; set `PORT` or `ASPNETCORE_URLS` and all env vars from `.env.example`.

## Frontend after deploy

Set in your frontend hosting (e.g. Vercel):

```
VITE_API_URL=https://your-api-host.onrender.com
```

Rebuild/redeploy the frontend so API and SignalR (`/hubs/collaboration`) hit the live backend.

## Project structure

- `Controllers/` — REST API
- `Hubs/CollaborationHub.cs` — SignalR
- `Services/` — JWT, email, WaafiPay, OpenAI, templates seed
- `Data/MongoDbContext.cs` — Neon Postgres JSONB storage
