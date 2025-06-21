// Copy request
function copyRequest() {
    const params = inputMode === 'form' ? collectParamValues() : JSON.parse(document.getElementById('requestBody').value);
    const request = {
        jsonrpc: "2.0",
        method: currentMethod.name,
        params: params,
        id: Date.now()
    };
    
    copyToClipboard(JSON.stringify(request, null, 2));
    
    // Show feedback
    const button = event.target;
    const originalText = button.textContent;
    button.textContent = 'Copied!';
    setTimeout(() => {
        button.textContent = originalText;
    }, 2000);
}

// Execute request
async function executeRequest() {
    if (!currentMethod || !selectedServer) return;
    
    const responseViewer = document.getElementById('responseViewer');
    
    // Show loading
    responseViewer.innerHTML = '<div class="loading">Executing request...</div>';
    showTab('response');
    
    try {
        // Get params based on input mode
        const params = inputMode === 'form' 
            ? collectParamValues() 
            : JSON.parse(document.getElementById('requestBody').value);
        
        // Build JSON-RPC request
        const rpcRequest = {
            jsonrpc: "2.0",
            method: currentMethod.name,
            params: params,
            id: Date.now()
        };
        
        console.log('Sending request:', rpcRequest);
        
        // Execute request
        const response = await fetch(selectedServer.url, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            body: JSON.stringify(rpcRequest)
        });
        
        const result = await response.json();
        
        // Display response with actions
        const responseData = JSON.stringify(result, null, 2);
        responseViewer.innerHTML = `
            <div style="margin-bottom: 10px;">
                <strong>Response:</strong>
                <button class="copy-button" id="copy-response-btn">Copy</button>
                <pre>${escapeHtml(responseData)}</pre>
            </div>
        `;
        
        // Add click handler after element is created
        document.getElementById('copy-response-btn').addEventListener('click', function() {
            copyToClipboard(responseData);
        });
        
    } catch (error) {
        responseViewer.innerHTML = `
            <div style="color: #ff6b6b;">
                <strong>Error:</strong> ${error.message}
            </div>
        `;
    }
}