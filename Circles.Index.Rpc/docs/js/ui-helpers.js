// Render method details
function renderMethodDetails() {
    const content = document.getElementById('content');
    // Use the getCategory function to determine the tag
    const tagName = getCategory(currentMethod.name);
    const tagClass = tagName.toLowerCase().replace(/ /g, '').replace('/', '');
    
    content.innerHTML = `
        <div class="content-card">
            <div style="margin-bottom: 20px;">
                <span class="tag ${tagClass}">${tagName}</span>
                <h2 style="margin-top: 10px;">${currentMethod.name}</h2>
                <p style="color: #718096; margin-top: 10px;">${currentMethod.description}</p>
            </div>
            
            <div class="tabs">
                <div class="tab active" onclick="showTab('request')">Request</div>
                <div class="tab" onclick="showTab('response')">Response</div>
                <div class="tab" onclick="showTab('examples')">Examples</div>
            </div>
            
            <div class="tab-content active" id="request-tab">
                <div class="params-section">
                    <h3 class="params-title">Parameters</h3>
                    
                    <div class="input-mode-toggle">
                        <label>
                            <input type="radio" name="inputMode" value="form" checked 
                                onchange="setInputMode('form')">
                            Form Input
                        </label>
                        <label>
                            <input type="radio" name="inputMode" value="raw" 
                                onchange="setInputMode('raw')">
                            Raw JSON
                        </label>
                    </div>
                    
                    <div id="form-inputs" style="display: block;">
                        ${currentMethod.params.length === 0 ? 
                            '<div class="no-params">This method has no parameters.</div>' :
                            generateCompactParamInputs(currentMethod.params)
                        }
                    </div>
                    
                    <div id="raw-input" style="display: none;">
                        <textarea class="code-editor" id="requestBody">${generateDefaultParams()}</textarea>
                    </div>
                </div>
                
                <div style="margin-top: 20px;">
                    <button class="button" onclick="executeRequest()">Execute Request</button>
                    <button class="button secondary" onclick="copyRequest()">Copy Request</button>
                </div>
            </div>
            
            <div class="tab-content" id="response-tab">
                <h3 style="margin-bottom: 15px; font-size: 18px; font-weight: 600;">Response</h3>
                <div id="responseViewer" class="response-viewer">
                    <div style="color: #718096; text-align: center; padding: 40px;">
                        Execute a request to see the response here
                    </div>
                </div>
            </div>
            
            <div class="tab-content" id="examples-tab">
                <h3 style="margin-bottom: 15px;">Examples</h3>
                ${renderExamples()}
            </div>
        </div>
    `;
    
    // Initialize array inputs with default values if any
    setTimeout(() => {
        currentMethod.params.forEach((param, index) => {
            if (getParamType(param.schema).endsWith('[]') && param.default) {
                const paramId = `param-${index}`;
                param.default.forEach(val => addArrayItem(paramId, val));
            }
        });
        
        // Initialize example copy buttons
        const examples = getMethodExamples(currentMethod.name);
        examples.forEach((example, idx) => {
            const btn = document.getElementById(`example-copy-${idx}`);
            if (btn) {
                btn.addEventListener('click', function() {
                    copyToClipboard(JSON.stringify(example.request, null, 2));
                });
            }
        });
    }, 100);
}

// Generate compact parameter input fields
function generateCompactParamInputs(params) {
    return params.map((param, index) => {
        const paramType = getParamType(param.schema);
        const paramId = `param-${index}`;
        
        let inputHtml = '';
        
        if (paramType === 'boolean') {
            inputHtml = `
                <div class="checkbox-container">
                    <input type="checkbox" id="${paramId}" 
                        ${param.default === true ? 'checked' : ''}>
                    <label for="${paramId}">Enabled</label>
                </div>
            `;
        } else if (paramType.endsWith('[]')) {
            // Array input
            inputHtml = `
                <div class="array-input-container" id="${paramId}-container">
                    <div class="array-items" id="${paramId}-items"></div>
                    <button class="add-array-item" onclick="addArrayItem('${paramId}')">
                        + Add Item
                    </button>
                </div>
            `;
        } else if (paramType === 'Address') {
            inputHtml = `
                <input type="text" 
                    id="${paramId}" 
                    class="param-input" 
                    placeholder="0x..." 
                    pattern="^0x[a-fA-F0-9]{40}$"
                    value="${param.default || ''}"
                    title="Ethereum address (0x followed by 40 hex characters)">
            `;
        } else if (paramType === 'integer' || paramType === 'number') {
            inputHtml = `
                <input type="number" 
                    id="${paramId}" 
                    class="param-input" 
                    placeholder="Enter number"
                    value="${param.default !== undefined ? param.default : ''}">
            `;
        } else if (paramType === 'SelectDto' || paramType === 'object') {
            // Complex object - use textarea
            const defaultObj = param.default || {
                namespace: "CrcV1",
                table: "Transfer",
                columns: ["from", "to", "value"],
                filter: [],
                limit: 10
            };
            inputHtml = `
                <textarea id="${paramId}" 
                    class="param-input" 
                    rows="4"
                    placeholder="Enter JSON object">${JSON.stringify(defaultObj, null, 2)}</textarea>
            `;
        } else {
            // Default text input
            inputHtml = `
                <input type="text" 
                    id="${paramId}" 
                    class="param-input" 
                    placeholder="${param.default || `Enter ${paramType.toLowerCase()}`}"
                    value="${param.default || ''}">
            `;
        }
        
        return `
            <div class="param-item">
                <div class="param-header">
                    <div class="param-name">
                        ${param.name}
                        <span class="param-type">${paramType}</span>
                    </div>
                    <div class="${param.required ? 'param-required' : 'param-optional'}">
                        ${param.required ? 'required' : 'optional'}
                    </div>
                </div>
                <div class="param-input-container">
                    ${inputHtml}
                    ${param.description ? `<div class="param-description">${param.description}</div>` : ''}
                </div>
            </div>
        `;
    }).join('');
}

// Add array item
function addArrayItem(paramId, value = '') {
    const container = document.getElementById(`${paramId}-items`);
    const itemId = `${paramId}-item-${Date.now()}`;
    
    const itemHtml = `
        <div class="array-item" id="${itemId}">
            <input type="text" 
                class="param-input" 
                value="${value}"
                placeholder="Enter value">
            <button onclick="removeArrayItem('${itemId}')">Remove</button>
        </div>
    `;
    
    container.insertAdjacentHTML('beforeend', itemHtml);
}

// Remove array item
function removeArrayItem(itemId) {
    document.getElementById(itemId).remove();
}

// Get parameter type display
function getParamType(schema) {
    if (!schema) return 'any';
    if (schema.$ref) {
        const refParts = schema.$ref.split('/');
        return refParts[refParts.length - 1];
    }
    if (schema.type === 'array' && schema.items) {
        const itemType = schema.items.$ref ? 
            schema.items.$ref.split('/').pop() : 
            schema.items.type;
        return `${itemType}[]`;
    }
    return schema.type || 'any';
}

// Generate default params for raw mode
function generateDefaultParams() {
    if (!currentMethod || currentMethod.params.length === 0) return '[]';
    
    const paramValues = currentMethod.params.map(param => {
        if (param.default !== undefined && param.default !== null) {
            return param.default;
        }
        if (param.schema?.type === 'string' || param.schema?.$ref === '#/components/schemas/Address') {
            return "";
        }
        if (param.schema?.type === 'integer') return 0;
        if (param.schema?.type === 'boolean') return false;
        if (param.schema?.type === 'array') return [];
        if (param.schema?.type === 'object') return {};
        return null;
    });
    
    return JSON.stringify(paramValues, null, 2);
}

// Collect parameter values from form inputs
function collectParamValues() {
    if (!currentMethod) return [];
    
    return currentMethod.params.map((param, index) => {
        const paramId = `param-${index}`;
        const paramType = getParamType(param.schema);
        
        if (paramType === 'boolean') {
            const checkbox = document.getElementById(paramId);
            return checkbox ? checkbox.checked : param.default || false;
        } else if (paramType.endsWith('[]')) {
            // Collect array values
            const items = document.querySelectorAll(`#${paramId}-items input`);
            const values = Array.from(items).map(input => {
                const val = input.value.trim();
                // Parse based on array element type
                if (paramType === 'Address[]') {
                    return val;
                } else if (paramType === 'string[]') {
                    return val;
                }
                return val;
            }).filter(v => v !== '');
            
            return values.length > 0 ? values : (param.default || []);
        } else {
            const input = document.getElementById(paramId);
            if (!input) return param.default || null;
            
            const value = input.value.trim();
            if (value === '' && !param.required) {
                return param.default !== undefined ? param.default : null;
            }
            
            // Parse based on type
            if (paramType === 'integer' || paramType === 'number') {
                return value !== '' ? Number(value) : (param.default || 0);
            } else if (paramType === 'SelectDto' || paramType === 'object') {
                try {
                    return JSON.parse(value);
                } catch {
                    return param.default || {};
                }
            }
            
            return value || param.default || null;
        }
    });
}

// Render examples
function renderExamples() {
    const examples = getMethodExamples(currentMethod.name);
    
    if (examples.length === 0) {
        return '<p style="color: #718096;">No examples available for this method.</p>';
    }
    
    let html = '';
    examples.forEach((example, idx) => {
        html += `
            <div class="example-card">
                <div class="example-title">${example.name}</div>
                <div style="margin-bottom: 10px;">
                    <strong>Request:</strong>
                    <button class="copy-button" id="example-copy-${idx}">Copy</button>
                    <pre>${JSON.stringify(example.request, null, 2)}</pre>
                </div>
                <div>
                    <strong>Response:</strong>
                    <pre>${JSON.stringify(example.response, null, 2)}</pre>
                </div>
            </div>
        `;
    });
    
    // Add click handlers after rendering
    setTimeout(() => {
        examples.forEach((example, idx) => {
            const btn = document.getElementById(`example-copy-${idx}`);
            if (btn) {
                btn.addEventListener('click', function() {
                    copyToClipboard(JSON.stringify(example.request, null, 2));
                });
            }
        });
    }, 100);
    
    return html;
}

// Escape HTML
function escapeHtml(text) {
    const map = {
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#039;'
    };
    return text.replace(/[&<>"']/g, m => map[m]);
}

// Copy to clipboard
function copyToClipboard(text) {
    const textarea = document.createElement('textarea');
    textarea.value = text;
    textarea.style.position = 'fixed';
    textarea.style.left = '-9999px';
    document.body.appendChild(textarea);
    textarea.select();
    
    try {
        document.execCommand('copy');
        // Show feedback on the button that was clicked
        if (event && event.target) {
            const button = event.target;
            const originalText = button.textContent;
            button.textContent = 'Copied!';
            setTimeout(() => {
                button.textContent = originalText;
            }, 2000);
        }
    } catch (err) {
        console.error('Failed to copy:', err);
    }
    
    document.body.removeChild(textarea);
}