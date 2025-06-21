#!/usr/bin/env dotnet-script

using System;
using System.IO;
using System.Net;
using System.Text;

var port = 8000;
var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{port}/");

Console.WriteLine($"Serving at http://localhost:{port}");
Console.WriteLine("Press Ctrl+C to stop");

listener.Start();

// Open browser
try {
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
        FileName = $"http://localhost:{port}",
        UseShellExecute = true
    });
} catch { }

while (true) {
    var context = listener.GetContext();
    var request = context.Request;
    var response = context.Response;
    
    var path = request.Url.LocalPath == "/" ? "/index.html" : request.Url.LocalPath;
    var filePath = "." + path;
    
    if (File.Exists(filePath)) {
        var content = File.ReadAllBytes(filePath);
        response.ContentType = path.EndsWith(".json") ? "application/json" : "text/html";
        response.ContentLength64 = content.Length;
        response.OutputStream.Write(content, 0, content.Length);
    } else {
        response.StatusCode = 404;
    }
    
    response.Close();
}