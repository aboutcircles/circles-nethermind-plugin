// Get method examples
function getMethodExamples(methodName) {
    const examples = [];
    
    // Add comprehensive examples for each method
    if (methodName === 'circles_getTotalBalance') {
        examples.push({
            name: 'Get balance in time circles',
            request: {
                jsonrpc: "2.0",
                method: "circles_getTotalBalance",
                params: ["0xde374ece6fa50e781e81aac78e811b33d16912c7", true],
                id: 1
            },
            response: {
                jsonrpc: "2.0",
                result: "1234.567890123456789",
                id: 1
            }
        });
        examples.push({
            name: 'Get balance in static circles',
            request: {
                jsonrpc: "2.0",
                method: "circles_getTotalBalance",
                params: ["0xde374ece6fa50e781e81aac78e811b33d16912c7", false],
                id: 2
            },
            response: {
                jsonrpc: "2.0",
                result: "1000000000000000000000",
                id: 2
            }
        });
    } else if (methodName === 'circles_getAvatarInfo') {
        examples.push({
            name: 'Get avatar information',
            request: {
                jsonrpc: "2.0",
                method: "circles_getAvatarInfo",
                params: ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
                id: 1
            },
            response: {
                jsonrpc: "2.0",
                result: {
                    version: 1,
                    type: "CrcV1_Signup",
                    avatar: "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                    tokenId: "0xabc123...",
                    hasV1: true,
                    v1Token: "0xdef456...",
                    cidV0Digest: "",
                    cidV0: "QmXxx...",
                    isHuman: true,
                    name: null,
                    symbol: null
                },
                id: 1
            }
        });
    } else if (methodName === 'circles_query') {
        examples.push({
            name: 'Query recent transfers',
            request: {
                jsonrpc: "2.0",
                method: "circles_query",
                params: [{
                    namespace: "CrcV1",
                    table: "Transfer",
                    columns: ["from", "to", "value", "blockNumber"],
                    filter: [{
                        column: "blockNumber",
                        filterType: "GreaterThan",
                        value: 30000000
                    }],
                    order: [{
                        column: "blockNumber",
                        direction: "DESC"
                    }],
                    limit: 10
                }],
                id: 1
            },
            response: {
                jsonrpc: "2.0",
                result: {
                    columns: ["from", "to", "value", "blockNumber"],
                    rows: [
                        ["0x123...", "0x456...", "1000000000000000000", 30000100],
                        ["0x789...", "0xabc...", "2000000000000000000", 30000099]
                    ]
                },
                id: 1
            }
        });
    } else if (methodName === 'circles_events') {
        examples.push({
            name: 'Get events for an address',
            request: {
                jsonrpc: "2.0",
                method: "circles_events",
                params: [
                    "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                    30000000,
                    30100000,
                    ["CrcV1_Transfer", "CrcV1_Trust"],
                    null,
                    true
                ],
                id: 1
            },
            response: {
                jsonrpc: "2.0",
                result: [
                    {
                        event: "CrcV1_Transfer",
                        values: {
                            blockNumber: 30000100,
                            timestamp: 1640000000,
                            transactionIndex: 5,
                            logIndex: 10,
                            from: "0x123...",
                            to: "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                            value: "1000000000000000000"
                        }
                    }
                ],
                id: 1
            }
        });
    } else if (methodName === 'circles_getTrustRelations') {
        examples.push({
            name: 'Get trust relations for an address',
            request: {
                jsonrpc: "2.0",
                method: "circles_getTrustRelations",
                params: ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
                id: 1
            },
            response: {
                jsonrpc: "2.0",
                result: {
                    user: "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                    trusts: [
                        {
                            user: "0x123...",
                            limit: 100
                        },
                        {
                            user: "0x456...",
                            limit: 50
                        }
                    ],
                    trustedBy: [
                        {
                            user: "0x789...",
                            limit: 100
                        }
                    ]
                },
                id: 1
            }
        });
    } else if (methodName === 'circles_getTokenBalances') {
        examples.push({
            name: 'Get token balances',
            request: {
                jsonrpc: "2.0",
                method: "circles_getTokenBalances",
                params: ["0xde374ece6fa50e781e81aac78e811b33d16912c7"],
                id: 1
            },
            response: {
                jsonrpc: "2.0",
                result: [
                    {
                        tokenAddress: "0xabc...",
                        tokenId: "12345",
                        tokenOwner: "0xde374ece6fa50e781e81aac78e811b33d16912c7",
                        tokenType: "CrcV1_Signup",
                        version: 1,
                        attoCircles: "1000000000000000000000",
                        circles: 1000.0,
                        staticAttoCircles: "900000000000000000000",
                        staticCircles: 900.0,
                        attoCrc: "1100000000000000000000",
                        crc: 1100.0,
                        isErc20: true,
                        isErc1155: false,
                        isWrapped: false,
                        isInflationary: true,
                        isGroup: false
                    }
                ],
                id: 1
            }
        });
    } else if (methodName === 'circles_searchProfiles') {
        examples.push({
            name: 'Search for profiles',
            request: {
                jsonrpc: "2.0",
                method: "circles_searchProfiles",
                params: ["alice", 20, 0],
                id: 1
            },
            response: {
                jsonrpc: "2.0",
                result: [
                    {
                        address: "0x123...",
                        CID: "QmXxx...",
                        lastUpdatedAt: 1640000000,
                        name: "Alice",
                        description: "Community member",
                        registeredName: "alice.eth",
                        location: "Berlin",
                        imageUrl: "https://example.com/alice.jpg",
                        previewImageUrl: "https://example.com/alice-thumb.jpg",
                        geoLocation: [13.404954, 52.520008],
                        longitude: 13.404954,
                        latitude: 52.520008,
                        shortName: "alice"
                    }
                ],
                id: 1
            }
        });
    } else if (methodName === 'circlesV2_findPath') {
        examples.push({
            name: 'Find transfer path',
            request: {
                jsonrpc: "2.0",
                method: "circlesV2_findPath",
                params: [{
                    from: "0x123...",
                    to: "0x456...",
                    amount: "1000000000000000000"
                }],
                id: 1
            },
            response: {
                jsonrpc: "2.0",
                result: {
                    maxFlow: "1000000000000000000",
                    paths: [
                        {
                            from: "0x123...",
                            to: "0x456...",
                            token: "0xabc...",
                            amount: "1000000000000000000"
                        }
                    ]
                },
                id: 1
            }
        });
    } else if (methodName === 'circles_health') {
        examples.push({
            name: 'Check health status',
            request: {
                jsonrpc: "2.0",
                method: "circles_health",
                params: [],
                id: 1
            },
            response: {
                jsonrpc: "2.0",
                result: "Healthy",
                id: 1
            }
        });
    } else if (methodName === 'circles_tables') {
        examples.push({
            name: 'Get available tables',
            request: {
                jsonrpc: "2.0",
                method: "circles_tables",
                params: [],
                id: 1
            },
            response: {
                jsonrpc: "2.0",
                result: [
                    {
                        namespace: "CrcV1",
                        tables: [
                            {
                                table: "Transfer",
                                topic: "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef",
                                columns: [
                                    { column: "from", type: "Address" },
                                    { column: "to", type: "Address" },
                                    { column: "value", type: "UInt256" }
                                ]
                            }
                        ]
                    }
                ],
                id: 1
            }
        });
    }
    
    return examples;
}