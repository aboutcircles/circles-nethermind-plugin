#!/bin/bash
# Setup git hooks for development
# Run this after cloning the repository

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
HOOKS_DIR="$REPO_ROOT/.git/hooks"
SOURCE_HOOKS_DIR="$SCRIPT_DIR/git-hooks"

echo "Setting up git hooks..."

# Ensure .git/hooks directory exists
mkdir -p "$HOOKS_DIR"

# Install pre-push hook
if [ -f "$SOURCE_HOOKS_DIR/pre-push" ]; then
    ln -sf "$SOURCE_HOOKS_DIR/pre-push" "$HOOKS_DIR/pre-push"
    echo "✓ Installed pre-push hook"
fi

# Install pre-commit hook
if [ -f "$SOURCE_HOOKS_DIR/pre-commit" ]; then
    ln -sf "$SOURCE_HOOKS_DIR/pre-commit" "$HOOKS_DIR/pre-commit"
    echo "✓ Installed pre-commit hook"
fi

echo ""
echo "Git hooks installed successfully!"
echo "Hooks will run automatically on git operations."
echo ""
echo "To bypass hooks (not recommended): git push --no-verify"
