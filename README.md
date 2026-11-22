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

If a generated query has a SQLite syntax or schema error, the app asks the model once to correct it. If the retry also fails, the error is shown with a **Retry question** action that resubmits the original question.

Select **Learn reports** in the sidebar to open the in-app guide explaining the report controls, request flow, query safeguards, and troubleshooting.

Without `BUSINESS_DB_PATH`, the report composer offers two built-in databases: **Sales demo** and **Access control demo**. On first run, the app creates `data/business-demo.sqlite` with sample sales data and `data/access-control-demo.sqlite` with synthetic cardholder, site, door, reader, swipe, activity, and attendance data. The seed scripts do not overwrite existing rows. These databases are for learning; they are not real business data.

To use a different local SQLite file, set `BUSINESS_DB_PATH` to its absolute path before starting the app. The configured database must already exist and is opened read-only; the app does not create or alter its schema. The question and table/column names are sent to the configured Ollama server to produce SQL; by default Ollama runs locally at `http://localhost:11434`. Query results are limited to 100 rows, and write operations are rejected.

```sh
BUSINESS_DB_PATH=/absolute/path/to/business.sqlite dotnet run
```

The database selector applies to each report request and schema view; switching it does not require restarting the app. The report API is documented at `/openapi/v1.json` in Development. [`BusinessReports.http`](./BusinessReports.http) contains example schema and report requests. The schema supplied to the model includes table columns and declared foreign-key join paths.

### Test with access-control data

The repository includes a synthetic access-control database script with cardholders, cards, sites, doors, readers, swipes, door activities, cardholder activities, and daily attendance records. It contains fictional data only.

Choose **Access control demo** in the report composer to test it from the UI. To exercise the API examples directly, start the app without `BUSINESS_DB_PATH`, then use [`AccessControlReports.http`](./AccessControlReports.http), which selects the built-in access-control database by ID.

The denied-swipe example should return **North Campus: 2**; the attendance example for `2026-11-16` should return four cardholders. When `BUSINESS_DB_PATH` is set, the built-in selector is replaced by that single configured database; the configured file must already exist and is not modified.

CPU and memory monitoring is supported on macOS and Linux. Expand **Top memory users** to see processes grouped by name and their resident memory, with a reminder when overall memory use is high. The app only reports and suggests; it does not stop processes. Ollama reports active model memory allocation (including VRAM where available); GPU utilization is hardware- and driver-specific and is not reported by Ollama on all systems.

## Notes

- Keep the app bound to localhost unless you intentionally want other devices on your network to access it.
- The app does not add authentication. Do not expose it to an untrusted network.
- Chats are not saved after the page is refreshed.
