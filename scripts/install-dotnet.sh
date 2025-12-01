#!/bin/bash
# Install .NET 10 SDK for Claude Code web sessions

# Only run in remote Claude Code environments
if [ "$CLAUDE_CODE_REMOTE" != "true" ]; then
  echo "Skipping .NET SDK install (not in Claude Code remote environment)"
  exit 0
fi

set -e

echo "Installing .NET 10 SDK..."

# Download and run the official .NET install script
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh

# Install .NET 10 SDK (preview channel)
/tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"

# Add to PATH for this session
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"

# Persist environment variables for the Claude Code session
if [ -n "$CLAUDE_ENV_FILE" ]; then
  echo "DOTNET_ROOT=$HOME/.dotnet" >> "$CLAUDE_ENV_FILE"
  echo "PATH=$HOME/.dotnet:\$PATH" >> "$CLAUDE_ENV_FILE"
fi

# Verify installation
echo "Installed .NET version:"
"$HOME/.dotnet/dotnet" --version

echo ".NET 10 SDK installation complete!"
exit 0
