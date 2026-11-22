# HelloOllama

A local .NET web app for chatting with Ollama, managing models, asking read-only business questions against SQLite or SQL Server, and monitoring system resources.

<img width="1451" height="734" alt="image" src="https://github.com/user-attachments/assets/8321a7ce-4ea0-42c6-8132-33447a3264a6" />


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

The app connects directly to Ollama's HTTP API. The browser keeps the current chat conversation and generation settings locally. Choose **Dark**, **Light**, or **System** from the theme selector in the top bar; the preference is shared with the learning guide and saved in this browser. The **Load/Offload Models** menu provides pull, show, copy, offload, and delete actions; pulling offers popular models plus an **Other** option for custom names. The Parameters panel also shows loaded models and lets you offload one. The app does not execute arbitrary shell commands.

## Database Chat

Select **Database Chat** in the sidebar. Ask a question in natural language, choose an installed Ollama model, and select a database. The model generates a provider-specific report query; the app validates and runs it against a read-only database connection, then displays the result table and SQL.

If a generated query has a database syntax or schema error the app recognizes, it asks the model once to correct it. If the retry also fails, the error is shown with a **Retry question** action that resubmits the original question.

Select **Database connections** in the sidebar to connect an existing database. Choose SQLite and provide a connection string such as `Data Source=/absolute/path/to/reporting.sqlite`, or choose SQL Server and provide a connection string such as `Server=localhost,1433;Database=Reporting;User Id=report_reader;Password=...;Encrypt=True`. The connection is checked before the profile is saved. Saved connection strings are encrypted with ASP.NET Core Data Protection in the current user's local application data; encryption keys are kept in the same private app-data directory. Keep a backup of that directory if you need to preserve profiles across machine moves. If a saved database becomes unavailable, HelloOllama still starts and reports the connection/schema error; restore access or remove the profile. Removing a saved profile only unregisters it from HelloOllama; it never deletes or modifies the database.

For a saved profile, open **Chat Safety Settings**, select a local model, and choose **Generate basic settings** to draft context, hints, validation guidance, checklist items, and test prompts from the schema. This replaces the fields in the editor but does not save them until you review and choose **Save with profile**. You can also generate or edit test prompts separately. Context, hints, and validation guidance are included with the schema when generating reports; validation notes guide the model but are not enforced by the application. Checklist items are saved with the profile but are not sent to Ollama. Saved prompts appear above Database Chat when that profile is selected. Built-in demos and `BUSINESS_DB_PATH` profiles are not editable this way.

The report composer always keeps the built-in **Sales demo** and **Access control demo** available. On first run, the app creates `data/business-demo.sqlite` with sample sales data and `data/access-control-demo.sqlite` with synthetic cardholder, site, door, reader, swipe, activity, and attendance data. The seed scripts do not overwrite existing rows. These databases are for learning; they are not real business data.

Alternatively, set `BUSINESS_DB_PATH` to an existing SQLite file before starting the app. It is added as a non-removable database and becomes the default selection; the built-in demos remain available. The file is opened read-only and the app does not create or alter its schema.

```sh
BUSINESS_DB_PATH=/absolute/path/to/business.sqlite dotnet run
```

The database selector applies to each report request and schema view; switching it does not require restarting the app. The question and table/column names are sent to the configured Ollama server; by default Ollama runs locally at `http://localhost:11434`. Query results are limited to 100 rows. The report API is documented at `/openapi/v1.json` in Development. [`BusinessReports.http`](./BusinessReports.http) contains example schema and report requests. The schema supplied to the model includes table columns and declared foreign-key join paths.

For SQL Server, use a dedicated account granted only the `SELECT` permissions required for reporting. `ApplicationIntent=ReadOnly` is set as a routing hint, not as an authorization control; query validation is not a substitute for database permissions. Do not use an administrator account.

### Test with access-control data

The repository includes a synthetic access-control database script with cardholders, cards, sites, doors, readers, swipes, door activities, cardholder activities, and daily attendance records. It contains fictional data only.

Choose **Access control demo** in Database Chat to test it from the UI. To exercise the API examples directly, start the app without `BUSINESS_DB_PATH`, then use [`AccessControlReports.http`](./AccessControlReports.http), which selects the built-in access-control database by ID.

The denied-swipe example should return **North Campus: 2**; the attendance example for `2026-11-16` should return four cardholders. When `BUSINESS_DB_PATH` is set, its database is added to the selector and selected by default; the built-in databases remain available.

CPU and memory monitoring is supported on macOS and Linux. Expand **Top memory users** to see processes grouped by name and their resident memory, with a reminder when overall memory use is high. The app only reports and suggests; it does not stop processes. Ollama reports active model memory allocation (including VRAM where available); GPU utilization is hardware- and driver-specific and is not reported by Ollama on all systems.

## Notes

- Keep the app bound to localhost unless you intentionally want other devices on your network to access it.
- The app does not add authentication. Do not expose it to an untrusted network.
- Chats are not saved after the page is refreshed.
