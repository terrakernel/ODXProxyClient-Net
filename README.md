# ODX Proxy Client for .NET

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](https://opensource.org/licenses/MIT)

A modern, performant, and strongly-typed .NET client library for the [ODXProxy Gateway](https://www.odxproxy.io/). This package simplifies interaction with Odoo instances by providing an idiomatic C# interface over the Odoo JSON-RPC API.

### ✨ Key Features

*   **Modern .NET:** Built on .NET 8 with full support for `async`/`await`.
*   **Strongly-Typed:** Deserialize Odoo responses directly into your C# objects and records.
*   **Full API Coverage:** Simple methods for all common Odoo operations like `SearchReadAsync`, `CreateAsync`, `WriteAsync`, and `CallMethodAsync`.
*   **Robust Error Handling:** Includes custom exceptions to gracefully handle API, network, and Odoo-specific errors.
*   **Lightweight & Performant:** Designed to be lean and responsive, using `HttpClient` efficiently.

---

## 🚀 Getting Started

### 1. Installation

Install the package from the .NET CLI:

```shell
dotnet add package ODXProxy.Client
```

Or from the NuGet Package Manager console:

```shell
Install-Package ODXProxy.Client
```

### 2. Initialization

The client must be initialized once, typically at your application's startup.

```csharp
using ODXProxy.Client;
using ODXProxy.Client.Models;

// 1. Configure your Odoo instance and gateway credentials
var odooInstance = new ODXInstanceInfo(
    Url: "https://your-odoo-instance.com",
    UserId: 1,
    Db: "your_database_name",
    ApiKey: "your-odoo-user-api-key" // Odoo user's API key
);

var clientInfo = new ODXProxyClientInfo(
    Instance: odooInstance,
    OdxApiKey: "your-odx-gateway-api-key" // ODX Gateway API key
);

// 2. Initialize the client
// This should be called only once in your application's lifecycle.
ODXApiClient.Init(clientInfo);
```

### 3. Basic Usage: Fetching Data

After initialization, you can call API methods from anywhere in your application.

```csharp
// Define a record to match the data you're fetching
public record Partner(int id, string? name, string? email);

// Define the parameters for your request
var keyword = new ODXClientKeywordRequest
{
    Fields = new[] { "id", "name", "email" },
    Limit = 5,
    Context = new ODXClientRequestContext { Timezone = "UTC" }
};

var domain = new object[]
{
    new object[] { "is_company", "=", true }
};

// Call the API and process the response
var response = await ODXApiClient.SearchReadAsync<Partner>(
    model: "res.partner",
    domain: domain,
    keyword: keyword
);

if (response.Result is not null)
{
    Console.WriteLine($"Success! Found {response.Result.Length} partners.");
    foreach (var partner in response.Result)
    {
        Console.WriteLine($"  -> ID: {partner.id}, Name: {partner.name}");
    }
}
else if (response.Error is not null)
{
    Console.WriteLine($"An Odoo error occurred: {response.Error.Message}");
}
```

---

## API Overview

All methods are available as static members of the `ODXApiClient` class.

| Method | Description | Common Return Type |
| :--- | :--- | :--- |
| **`SearchReadAsync<T>`** | Performs a search with a domain and returns the requested fields for matching records. | `T[]` |
| **`SearchAsync`** | Performs a search and returns only the IDs of matching records. | `int[]` |
| **`SearchCountAsync`** | Counts the number of records matching a search domain. | `int` |
| **`ReadAsync<T>`** | Retrieves records by their specific IDs. | `T[]` |
| **`CreateAsync`** | Creates a single new record. | `int` (the new record's ID) |
| **`WriteAsync`** | Updates one or more records with new values. | `bool` |
| **`RemoveAsync`** | Deletes (unlinks) one or more records by their IDs. | `bool` |
| **`CallMethodAsync<T>`** | Calls any public method on an Odoo model with specified parameters. | `T` (method-dependent) |
| **`FieldsGetAsync<T>`** | Retrieves the definition and properties of a model's fields. | `T` (typically `Dictionary<string, object>`) |

---

## Error Handling

The library provides a structured way to handle errors. It's recommended to wrap API calls in a `try...catch` block.

```csharp
using ODXProxy.Client.Exceptions;

try
{
    var response = await ODXApiClient.SearchReadAsync<Partner>(/* ... */);

    // Handle Odoo-level errors (e.g., validation, access rights)
    if (response.Error is not null)
    {
        Console.WriteLine($"API Error: {response.Error.Message}");
        // Inspect response.Error.Data for more details
    }
}
catch (ODXProxyException ex)
{
    // Handle client-level or network errors (e.g., 4xx/5xx status, timeouts, DNS issues)
    Console.WriteLine($"Client Exception! Status Code: {ex.StatusCode}");
    Console.WriteLine($"Message: {ex.Message}");
}
catch (Exception ex)
{
    // Handle any other unexpected errors
    Console.WriteLine($"An unexpected error occurred: {ex.Message}");
}
```

## Cancellation

All async methods accept an optional `CancellationToken` to support request cancellation, which is essential for building responsive applications.

```csharp
var cts = new CancellationTokenSource();
cts.CancelAfter(TimeSpan.FromSeconds(10)); // Set a 10-second timeout

try
{
    var partners = await ODXApiClient.SearchReadAsync<Partner>(
        model: "res.partner",
        domain: new object[0],
        keyword: new ODXClientKeywordRequest(),
        cancellationToken: cts.Token // Pass the token here
    );
}
catch (TaskCanceledException)
{
    Console.WriteLine("The request was canceled because it took too long.");
}
```

---

## Building from Source

To build and test the library locally:

1.  **Clone the repository:**
    ```shell
    git clone https://github.com/your-username/ODXProxyClient-Net.git
    cd ODXProxyClient-Net
    ```

2.  **Restore dependencies:**
    ```shell
    dotnet restore
    ```

3.  **Build the project:**
    ```shell
    dotnet build --configuration Release
    ```

## License

This project is licensed under the **MIT License**. See the [LICENSE](LICENSE) file for details.