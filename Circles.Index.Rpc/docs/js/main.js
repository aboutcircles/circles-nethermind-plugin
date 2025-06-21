// Global state
let openRpcDoc = null;
let currentMethod = null;
let selectedServer = null;
let inputMode = 'form'; // 'form' or 'raw'

// Initialize the application
document.addEventListener('DOMContentLoaded', function() {
    loadOpenRpcDoc();
    setupEventListeners();
});

// Load OpenRPC document
async function loadOpenRpcDoc(retries = 3) {
    try {
        const response = await fetch('circles-rpc.json', {
            cache: 'no-cache'
        });
        
        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }
        
        openRpcDoc = await response.json();
        selectedServer = openRpcDoc.servers[0];
        
        // Populate global server selector
        populateGlobalServerSelector();
        
        renderMethodList();
        
        // Show welcome message
        showWelcomeMessage();
    } catch (error) {
        console.error('Failed to load OpenRPC document:', error);
        
        if (retries > 0) {
            setTimeout(() => loadOpenRpcDoc(retries - 1), 1000);
        } else {
            showLoadError(error);
        }
    }
}

// Setup event listeners
function setupEventListeners() {
    // Search functionality
    document.getElementById('searchBox').addEventListener('input', renderMethodList);
}

// Show welcome message
function showWelcomeMessage() {
    document.getElementById('content').innerHTML = `
        <div class="content-card">
            <h2>Welcome to Circles RPC Documentation</h2>
            <p>Select a method from the sidebar to view its documentation and test it.</p>
            <div style="margin-top: 30px; padding: 20px; background: #f7fafc; border-radius: 8px;">
                <h3 style="margin-bottom: 15px; color: #2d3748;">Quick Start</h3>
                <p style="color: #4a5568; margin-bottom: 10px;">
                    This is an interactive JSON-RPC documentation for the Circles protocol. 
                    You can browse available methods, view their parameters, and execute test requests directly from this interface.
                </p>
                <p style="color: #4a5568;">
                    <strong>Selected Server:</strong> ${selectedServer.name}
                </p>
            </div>
        </div>
    `;
}

// Show load error
function showLoadError(error) {
    document.getElementById('content').innerHTML = `
        <div class="content-card">
            <div class="error">
                <h3>Failed to load OpenRPC document</h3>
                <p>${error.message}</p>
                <p style="margin-top: 10px;">Make sure circles-rpc.json exists in the same directory.</p>
                <button class="button" onclick="location.reload()" style="margin-top: 20px;">
                    Retry
                </button>
            </div>
        </div>
    `;
}

// Populate global server selector
function populateGlobalServerSelector() {
    const selector = document.getElementById('globalServerSelect');
    selector.innerHTML = openRpcDoc.servers.map((server, idx) => `
        <option value="${idx}" ${idx === 0 ? 'selected' : ''}>
            ${server.name} - ${server.url}
        </option>
    `).join('');
}

// Select server (global)
function selectServer(index) {
    selectedServer = openRpcDoc.servers[parseInt(index)];
    console.log('Selected server:', selectedServer);
    
    // Update global selector
    document.getElementById('globalServerSelect').value = index;
    
    // Show a subtle notification
    const serverStatus = document.querySelector('.server-status span:last-child');
    serverStatus.textContent = 'Switching...';
    setTimeout(() => {
        serverStatus.textContent = 'Connected';
    }, 500);
}

// Render method list in sidebar
function renderMethodList() {
    const methodList = document.getElementById('methodList');
    const searchTerm = document.getElementById('searchBox').value.toLowerCase();
    
    // Group methods by tag
    const grouped = {};
    openRpcDoc.methods.forEach(method => {
        // Use the getCategory function to determine the correct tag
        const tag = getCategory(method.name);
        if (!grouped[tag]) grouped[tag] = [];
        
        if (!searchTerm || 
            method.name.toLowerCase().includes(searchTerm) ||
            method.description.toLowerCase().includes(searchTerm)) {
            grouped[tag].push(method);
        }
    });
    
    // Render groups
    let html = '';
    Object.entries(grouped).forEach(([tag, methods]) => {
        if (methods.length === 0) return;
        
        html += `
            <div class="method-group">
                <div class="method-group-title">${tag}</div>
                ${methods.map(method => `
                    <div class="method-item" onclick="selectMethod('${method.name}')">
                        <div class="method-name">${method.name}</div>
                        <div class="method-description">${method.summary || method.description}</div>
                    </div>
                `).join('')}
            </div>
        `;
    });
    
    methodList.innerHTML = html;
}

// Select and display method
function selectMethod(methodName) {
    currentMethod = openRpcDoc.methods.find(m => m.name === methodName);
    if (!currentMethod) return;
    
    // Update active state
    document.querySelectorAll('.method-item').forEach(item => {
        item.classList.remove('active');
        if (item.querySelector('.method-name').textContent === methodName) {
            item.classList.add('active');
        }
    });
    
    // Render method details
    renderMethodDetails();
}

// Get category based on method name
function getCategory(methodName) {
    if (methodName.includes('V2') || methodName.includes('v2')) {
        return 'Circles v2';
    }
    // If no V1 or V2 is specified, it's for both versions
    return 'Circles v1/v2';
}

// Show tab
function showTab(tabName) {
    document.querySelectorAll('.tab').forEach(tab => {
        tab.classList.remove('active');
    });
    document.querySelectorAll('.tab-content').forEach(content => {
        content.classList.remove('active');
    });
    
    document.querySelector(`.tab:nth-child(${tabName === 'request' ? 1 : tabName === 'response' ? 2 : 3})`).classList.add('active');
    document.getElementById(`${tabName}-tab`).classList.add('active');
}

// Set input mode
function setInputMode(mode) {
    inputMode = mode;
    document.getElementById('form-inputs').style.display = mode === 'form' ? 'block' : 'none';
    document.getElementById('raw-input').style.display = mode === 'raw' ? 'block' : 'none';
    
    if (mode === 'raw') {
        // Update raw input with current form values
        const values = collectParamValues();
        document.getElementById('requestBody').value = JSON.stringify(values, null, 2);
    }
}