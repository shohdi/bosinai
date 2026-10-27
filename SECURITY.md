# Private local data

Wallet and API credentials belong in the operating system keyring or a local
secret store. The Python configuration tool uses Windows Credential Manager;
the repository does not need a wallet private key. Never put real credentials
in source code, documentation, examples, or the tracked application settings.

The tracked `config/settings.json` and `dotnet/Bonsai.Chat/appsettings.json`
contain public endpoints and ordinary application/model settings. The current
model tuning is preserved. Keep environment-specific credentials in ignored
environment files or local settings, rather than adding them to these files.

Ignored local material includes:

- `.env` and `.env.*` files, private keys/certificates and credential stores.
- `config/mcp.json`, local secret overrides and environment-specific .NET settings.
- Databases, journals, backups, logs, runtime keys and conversation history.
- `data`, `logs`, `runtime`, model weights, .NET `App_Data` and published binaries.

`config/mcp.example.json` contains a portable placeholder configuration for an
optional MCP client. The real local file stays outside Git. `stocks.xlsx` is a
public token-contract catalog, rather than a personal portfolio or wallet file.

The initial public snapshot was scanned with Gitleaks and independently reviewed
for credentials and private configuration. Local pre-publication history is
preserved separately; the public initial commit uses a GitHub noreply identity
to avoid exposing personal email metadata.

Ignore rules do not remove previously committed secrets. If a credential is ever
committed, revoke it and remove it from the published history before sharing.
