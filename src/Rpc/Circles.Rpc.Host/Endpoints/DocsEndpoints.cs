using System.Text.Json;
using System.Text.Json.Serialization;
using Circles.Rpc.Host.OpenRpc;

namespace Circles.Rpc.Host.Endpoints;

/// <summary>
/// Browser-facing documentation portal: <c>/</c> redirect, <c>/docs</c> HTML index,
/// <c>/openrpc.json</c> machine-readable spec, and <c>/openrpc</c> external playground redirect.
/// </summary>
public static class DocsEndpoints
{
    public static WebApplication MapDocsAndOpenRpc(this WebApplication app)
    {
        // ─── Browser root redirect ──────────────────────────────────────────────────
        app.MapGet("/", () => Results.Redirect("/docs")).ExcludeFromDescription();

        // ─── OpenRPC spec endpoint ───────────────────────────────────────────────────
        var openRpcJson = JsonSerializer.SerializeToUtf8Bytes(
            OpenRpcGenerator.Generate(),
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true });

        app.MapGet("/openrpc.json", () => Results.Bytes(openRpcJson, "application/json"))
            .ExcludeFromDescription();

        // Redirect /openrpc to the CirclesTools RPC Query View (comprehensive interactive playground)
        app.MapGet("/openrpc", () =>
            Results.Redirect("https://aboutcircles.github.io/CirclesTools/rpcQueryView.html"))
            .ExcludeFromDescription();

        // ─── Unified API documentation portal ───────────────────────────────────────
        app.MapGet("/docs", (HttpContext ctx) =>
        {
            var html = """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8"/>
              <meta name="viewport" content="width=device-width, initial-scale=1"/>
              <title>Circles API Documentation</title>
              <style>
                * { margin: 0; padding: 0; box-sizing: border-box; }
                body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif; background: #0a0e1a; color: #e0e0e0; min-height: 100vh; }
                .header { padding: 3rem 2rem 2rem; text-align: center; background: linear-gradient(135deg, #0d1117 0%, #161b22 100%); border-bottom: 1px solid #21262d; }
                .header h1 { font-size: 2rem; font-weight: 600; color: #f0f0f0; margin-bottom: 0.5rem; }
                .header p { color: #8b949e; font-size: 1.1rem; max-width: 600px; margin: 0 auto; }
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); gap: 1.5rem; padding: 2rem; max-width: 1200px; margin: 0 auto; }
                .card { background: #161b22; border: 1px solid #21262d; border-radius: 12px; padding: 1.5rem; transition: border-color 0.2s, transform 0.2s; }
                .card:hover { border-color: #388bfd; transform: translateY(-2px); }
                .card h2 { font-size: 1.2rem; color: #f0f0f0; margin-bottom: 0.5rem; display: flex; align-items: center; gap: 0.5rem; }
                .card p { color: #8b949e; font-size: 0.9rem; line-height: 1.5; margin-bottom: 1rem; }
                .badge { display: inline-block; font-size: 0.7rem; font-weight: 600; padding: 2px 8px; border-radius: 10px; text-transform: uppercase; }
                .badge-rpc { background: #1f3a5f; color: #58a6ff; }
                .badge-rest { background: #1a3f2a; color: #3fb950; }
                .badge-auth { background: #3d2e00; color: #d29922; }
                .links { display: flex; flex-wrap: wrap; gap: 0.5rem; }
                .links a { display: inline-flex; align-items: center; gap: 0.3rem; font-size: 0.85rem; color: #58a6ff; text-decoration: none; padding: 0.3rem 0.7rem; border: 1px solid #21262d; border-radius: 6px; transition: background 0.2s; }
                .links a:hover { background: #1f2937; }
                .footer { text-align: center; padding: 2rem; color: #484f58; font-size: 0.8rem; }
                .footer a { color: #58a6ff; text-decoration: none; }
              </style>
            </head>
            <body>
              <div class="header">
                <h1>Circles API Documentation</h1>
                <p>Machine-readable specs and interactive docs for all Circles protocol services</p>
              </div>
              <div class="grid">
                <div class="card">
                  <h2><span class="badge badge-rpc">JSON-RPC</span> Circles RPC API</h2>
                  <p>40+ JSON-RPC 2.0 methods for querying balances, avatars, profiles, trust relations, events, groups, invitations, and transitive transfer paths.</p>
                  <div class="links">
                    <a href="https://aboutcircles.github.io/CirclesTools/rpcQueryView.html">Interactive Playground</a>
                    <a href="/openrpc.json">openrpc.json</a>
                  </div>
                </div>
                <div class="card">
                  <h2><span class="badge badge-rest">REST</span> Pathfinder API</h2>
                  <p>Compute transitive transfer paths through the Circles trust network. Supports max flow, quantized mode, simulated balances, and debug stages.</p>
                  <div class="links">
                    <a href="/pathfinder/scalar/v1">Interactive Docs</a>
                    <a href="/pathfinder/openapi/v1.json">openapi.json</a>
                    <a href="https://aboutcircles.github.io/CirclesTools/rpcQueryView.html?method=circlesV2_findPath">Query Builder</a>
                  </div>
                </div>
                <div class="card">
                  <h2><span class="badge badge-auth">Auth</span> Authentication Service</h2>
                  <p>Sign-In with Ethereum (SIWE), passkey registration, JWT issuance, JWKS endpoint, and service-to-service authentication.</p>
                  <div class="links">
                    <a href="/auth/docs">Swagger UI</a>
                    <a href="/auth/openapi.json">openapi.json</a>
                  </div>
                </div>
                <div class="card">
                  <h2><span class="badge badge-rest">REST</span> Referrals API</h2>
                  <p>Invitation links, referral distributions, and at-scale onboarding backend for the Circles protocol.</p>
                  <div class="links">
                    <a href="/referrals/docs">Swagger UI</a>
                    <a href="/referrals/openapi.json">openapi.json</a>
                  </div>
                </div>
                <div class="card">
                  <h2><span class="badge badge-rest">REST</span> Profile Pinning Service</h2>
                  <p>IPFS profile storage, content pinning, full-text search, and CID resolution. Indexes profiles from on-chain NameRegistry events.</p>
                  <div class="links">
                    <a href="/profiles/docs">Swagger UI</a>
                    <a href="/profiles/openapi.json">openapi.json</a>
                  </div>
                </div>
                <div class="card">
                  <h2><span class="badge badge-rest">REST</span> Marketplace API</h2>
                  <p>Product catalogs, shopping carts, checkout, order fulfillment, and seller management. Supports Odoo and CodeDispenser adapters.</p>
                  <div class="links">
                    <a href="/market/docs">Swagger UI</a>
                    <a href="/market/docs/v1/swagger.json">openapi.json</a>
                  </div>
                </div>
                <div class="card">
                  <h2><span class="badge badge-rest">REST</span> Ethereum JSON-RPC</h2>
                  <p>Standard Ethereum JSON-RPC (eth_*, net_*, web3_*) proxied to Nethermind. Use for block queries, transaction submission, and chain state.</p>
                  <div class="links">
                    <a href="/chain-rpc/">Nethermind Dashboard</a>
                  </div>
                </div>
                <div class="card">
                  <h2><span class="badge badge-rest">Monitoring</span> Grafana</h2>
                  <p>Dashboards for indexer performance, RPC metrics, pathfinder load, cache hit rates, and infrastructure health.</p>
                  <div class="links">
                    <a href="/grafana">Grafana Dashboard</a>
                  </div>
                </div>
              </div>
              <div class="footer">
                <p>All specs are machine-readable — use them for client codegen, SDK generation, or AI agent tool discovery.<br/>
                <a href="https://github.com/aboutcircles">github.com/aboutcircles</a></p>
              </div>
            </body>
            </html>
            """;
            return Results.Content(html, "text/html");
        }).ExcludeFromDescription();

        return app;
    }
}
