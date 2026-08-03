# Configuration

Development:

- `Agent:DevelopmentKey` or `THAI_ID_AGENT_DEV_KEY` enables `X-Agent-Development-Key` authentication.
- `Agent:AllowedOrigins` must contain exact origins only.
- `http://localhost:3000` is allowed only by Development configuration.

Production:

- HTTP is disabled in Production.
- HTTPS binds to loopback port `18443` in Production. Development HTTPS is opt-in with `Agent:EnableHttpsInDevelopment=true`.
- JWT audience is `thai-id-card-agent`.
- JWT lifetime must be 60 seconds or less.
- Signing must happen outside the agent.

Default paths:

- Program: `C:\Program Files\ThaiIdCardAgent`
- Configuration: `C:\ProgramData\ThaiIdCardAgent\Config`
- Logs: `C:\ProgramData\ThaiIdCardAgent\Logs`
