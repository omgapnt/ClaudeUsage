# ClaudeUsage

A PowerToys Command Palette (CmdPal) extension that displays your Claude Code subscription usage as a live dock band and detailed stats page.

## Features

- **Dock band**: Live usage indicator showing session % left and weekly % left
- **Stats page**: Pretty breakdown of all usage limits with progress bars (unicode blocks)
- **Low battery alert**: Icon changes when session usage is over 80% (< 20% left)
- **Zero-phone-home**: Only communicates with Anthropic's official API; no telemetry

## Requirements

- Windows 11 (22H2 or later)
- PowerToys Command Palette (v0.9+)
- Claude Code signed in with valid OAuth token

## Installation

1. Clone/download this repo
2. Run `build-and-install.ps1` from a normal PowerShell prompt
3. UAC will prompt for elevation to install the MSIX package
4. Open Command Palette (Win+Alt+Space)
5. Enable "Claude usage" in Dock settings to see the live band

## How it Works

The extension reads your Claude Code OAuth token from `%USERPROFILE%\.claude\.credentials.json` and fetches live usage data from `https://api.anthropic.com/api/oauth/usage` every 5 minutes. No token is logged or displayed.

## Development

- Edit source files in `ClaudeUsage/` (C# 12, .NET 9.0-windows)
- Run `build-and-install.ps1` after changes
- Check PowerShell output for build errors

## License

Personal use extension.
