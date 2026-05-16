# Security Policy

## Supported Versions

HookVault is currently pre-1.0. Only the latest tagged release receives
security fixes.

## Reporting a Vulnerability

If you discover a security issue, please **do not** open a public issue.

Email `dnvasilev123@gmail.com` with:

- A clear description of the vulnerability
- Steps to reproduce (or a proof-of-concept)
- The affected version (HookVault tag or commit SHA)
- Your assessment of severity and blast radius

You can expect:

- An acknowledgement within 72 hours
- A coordinated disclosure plan within 7 days
- A fix landed in the next patch release where feasible

## Scope

HookVault is a **local development tool**. Its threat model assumes:

- The host is a trusted developer workstation or CI runner
- The captured webhook secrets are environment-scoped to that host
- The management API is not exposed to the public internet

Findings outside that threat model (e.g. "the API can be accessed by
anyone who reaches the port") are by-design and won't be treated as
vulnerabilities. Findings that break the model from inside it (e.g.
auth bypass, header injection, signature confusion) are in scope.
