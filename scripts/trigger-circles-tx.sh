#!/usr/bin/env bash
# trigger-circles-tx.sh
# Ensures dependencies are installed and runs trigger-circles-tx.ts

set -e  # Exit on any error

# Colors for output (optional, for better UX)
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Function to print colored output
print_status() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Check if Node.js is installed
if ! command -v node &> /dev/null; then
    print_error "Node.js is not installed. Please install Node.js (version 16 or higher) and try again."
    exit 1
fi

# Check if npm is installed
if ! command -v npm &> /dev/null; then
    print_error "npm is not installed. Please install npm and try again."
    exit 1
fi

print_status "Node.js and npm are available."

# Check if ethers is installed globally
if ! npm list -g ethers &> /dev/null; then
    print_warning "ethers is not installed globally. Installing..."
    npm install -g ethers
    print_status "ethers installed successfully."
else
    print_status "ethers is already installed globally."
fi

# Ensure the seed phrase is available before running the TypeScript script
if [[ -z "$CIRCLES_SEED_PHRASE" ]]; then
    print_error "CIRCLES_SEED_PHRASE is not set. Source your .env.local or export the variable before rerunning."
    exit 1
fi

# Get the directory of this script
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Path to the TypeScript script
TS_SCRIPT="$SCRIPT_DIR/trigger-circles-tx.ts"

# Check if the TypeScript script exists
if [[ ! -f "$TS_SCRIPT" ]]; then
    print_error "trigger-circles-tx.ts not found at $TS_SCRIPT"
    exit 1
fi

print_status "Running trigger-circles-tx.ts with provided arguments..."

# Run the TypeScript script with tsx, passing all arguments
exec npx tsx "$TS_SCRIPT" "$@"