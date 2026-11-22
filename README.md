# Ollama Desk

A local .NET web app for chatting with Ollama, managing models, asking read-only business questions against SQLite, and monitoring system resources.

## Requirements

- .NET 10 SDK
- [Ollama](https://ollama.com/) running on this computer, with at least one model available to chat

## Run

From this directory:

```sh
dotnet run
```

Open the local URL printed by ASP.NET Core, usually `http://localhost:5000` or `https://localhost:7000`.

If Ollama is listening somewhere other than `http://localhost:11434`, set `OLLAMA_BASE_URL` before starting the app:

```sh
OLLAMA_BASE_URL=http://localhost:11434 dotnet run
```

The app connects directly to Ollama's HTTP API. The browser keeps the current chat conversation and generation settings locally. The Commands dialog provides pull, show, copy, and delete actions using Ollama's API; pulling offers popular models plus an **Other** option for custom names. Use the **Loaded models** dropdown in the Parameters panel to select and offload a running model. The app does not execute arbitrary shell commands.

## Business reports

Select **Business reports** in the sidebar. Ask a question in natural language and choose an installed Ollama model. The model generates a SQLite report query; the app validates and runs it against a read-only database connection, then displays the result table and SQL.

On first run, the app creates `data/business-demo.sqlite` with sample sales data and tables for regions, customers, products, orders, and order items. It does not overwrite existing sample rows. This is demo data for learning; it is not your real business data.

To use a different local SQLite file, set `BUSINESS_DB_PATH` to its absolute path before starting the app. The configured database must already exist and is opened read-only; the app does not create or alter its schema. Ollama receives the question and schema locally to produce SQL. Query results are limited to 100 rows, and write operations are rejected.

```sh
BUSINESS_DB_PATH=/absolute/path/to/business.sqlite dotnet run
```

The report API is documented at `/openapi/v1.json` in Development. [`BusinessReports.http`](./BusinessReports.http) contains example schema and report requests.

CPU and memory monitoring is supported on macOS and Linux. Expand **Top memory users** to see processes grouped by name and their resident memory, with a reminder when overall memory use is high. The app only reports and suggests; it does not stop processes. Ollama reports active model memory allocation (including VRAM where available); GPU utilization is hardware- and driver-specific and is not reported by Ollama on all systems.

## Notes

- Keep the app bound to localhost unless you intentionally want other devices on your network to access it.
- The app does not add authentication. Do not expose it to an untrusted network.
- Chats are not saved after the page is refreshed.
