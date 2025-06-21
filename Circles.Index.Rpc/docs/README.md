# Circles RPC Documentation

Interactive documentation for the Circles Protocol RPC API, featuring a live testing interface and OpenRPC specification.

## 🚀 Quick Start

### View Documentation Locally

```bash
# Navigate to the docs directory
cd Circles.Index.Rpc/docs

# Run the local server
./serve.csx

# Or manually with dotnet-script
dotnet script serve.csx
```

The documentation will be served at `http://localhost:8000` and automatically open in your browser.

## 📚 Overview

This documentation system provides:

- **Interactive API Explorer**: Test RPC methods directly from the browser
- **OpenRPC Specification**: Machine-readable API specification in `circles-rpc.json`
- **Live Request Testing**: Execute real RPC calls against configured endpoints
- **Auto-generated from Source**: Documentation stays in sync with the actual implementation

## 🛠️ How It Works

### Documentation Generation

The documentation is generated in two parts:

1. **OpenRPC Generation** (`generate-openrpc.csx`):
   - Parses `ICirclesRpcModule.cs` for method signatures and attributes
   - Checks `CirclesRpcModule.cs` to verify which methods are implemented
   - Extracts method descriptions, parameters, and return types
   - Generates `circles-rpc.json` following the OpenRPC specification

2. **Interactive UI** (`index.html`):
   - Loads the generated `circles-rpc.json`
   - Provides a searchable method explorer
   - Offers form-based and raw JSON input modes
   - Executes real RPC requests and displays responses

### Key Features

- **Smart Parameter Forms**: Automatically generates appropriate input fields based on parameter types
- **Array Input Support**: Dynamic UI for array parameters
- **Request/Response Examples**: Pre-populated examples for common use cases
- **Multiple Server Support**: Switch between production and staging endpoints
- **Copy-to-Clipboard**: Easy sharing of requests and responses

## 🔧 Local Development

### Prerequisites

- .NET 8.0 or later
- dotnet-script tool

### Install dotnet-script

```bash
dotnet tool install -g dotnet-script
```

### Generate Documentation

```bash
# Navigate to docs directory
cd Circles.Index.Rpc/docs

# Generate OpenRPC specification
dotnet script generate-openrpc.csx

# Serve documentation locally
dotnet script serve.csx
```

### File Structure

```
Circles.Index.Rpc/docs/
├── index.html              # Interactive documentation UI
├── circles-rpc.json        # Generated OpenRPC specification
├── generate-openrpc.csx    # C# script to generate OpenRPC from source
├── serve.csx              # Simple HTTP server for local development
└── README.md              # This file
```

## 🤖 GitHub Actions

The documentation is automatically generated and deployed via GitHub Actions:

### Workflow: `.github/workflows/generate-docs.yml`

**Triggers:**
- Push to `main` or `master` branches when RPC files change
- Manual workflow dispatch

**Process:**
1. Checks out the repository
2. Sets up .NET 8.0 environment
3. Installs dotnet-script
4. Runs `generate-openrpc.csx` to create `circles-rpc.json`
5. Uploads documentation as GitHub Pages artifact
6. Deploys to GitHub Pages

### Manual Deployment

You can manually trigger the documentation generation:

1. Go to Actions tab in GitHub
2. Select "Generate OpenRPC Documentation"
3. Click "Run workflow"

## 📝 Adding New RPC Methods

To add a new RPC method to the documentation:

1. **Add to Interface** (`ICirclesRpcModule.cs`):
   ```csharp
   [JsonRpcMethod(
       Description = "Your method description",
       IsImplemented = true
   )]
   Task<ResultWrapper<YourReturnType>> your_methodName(
       YourParamType param1,
       AnotherType? param2 = null
   );
   ```

2. **Implement in Module** (`CirclesRpcModule.cs`):
   ```csharp
   public async Task<ResultWrapper<YourReturnType>> your_methodName(
       YourParamType param1,
       AnotherType? param2 = null)
   {
       // Implementation
   }
   ```

3. **Regenerate Documentation**:
   ```bash
   cd Circles.Index.Rpc/docs
   dotnet script generate-openrpc.csx
   ```

## 🎨 Customization

### Modifying the UI

Edit `index.html` to customize:
- Styling and colors
- Layout and components
- Example requests
- Server endpoints

### Extending Schema Types

Add new types in `generate-openrpc.csx`:

```csharp
["YourType"] = new {
    type = "object",
    description = "Your type description",
    properties = new {
        field1 = new { type = "string" },
        field2 = new { type = "integer" }
    }
}
```

## 🧪 Testing

### Verify Generation

```bash
# Generate docs
dotnet script generate-openrpc.csx

# Check if circles-rpc.json was created
ls -la circles-rpc.json

# Validate JSON
cat circles-rpc.json | jq .
```

### Test Locally

1. Run the server: `./serve.csx`
2. Open http://localhost:8000
3. Try executing some RPC methods
4. Verify responses match expectations




