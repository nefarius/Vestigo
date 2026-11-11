# Instructions for Codex and other coding agents

These instructions apply to the entire repository. Read them before editing.

## Branch preflight — required before the first change

1. Run `git status --short` and `git branch --show-current`.
2. Never implement or commit work on `master`.
3. Continue on an existing branch only when it belongs to the requested work.
   Otherwise create a new `feat/...` or `fix/...` branch from current `master`
   before editing any files, including documentation and these instructions.
4. If the worktree is dirty, stop and ask the user what to do before switching
   or creating a branch. Do not stash, discard, or carry changes without the
   user's explicit authorization. An instruction already given for those exact
   changes counts as authorization; do not ask again.
5. Before committing, verify the branch again and review the staged diff.
   Never merge or push to `master` unless explicitly requested.

## Existing repository rules

Read these files at the start of every task; their `alwaysApply` rules also
apply to Codex, even if the client does not automatically load `.mdc` files:

- `.cursor/rules/feature-branch-required.mdc`
- `.cursor/rules/vestigo-development.mdc`
- `.cursor/rules/mcp-docs.mdc`
- `.cursor/rules/windows-interop.mdc`

Consume released NuGet packages, preserve package boundaries, confine client
paths to configured roots, never log tokens, and save UTF-8 without BOM.

## Developing external plugins

When authoring plugins, their examples, or public plugin contracts, also read
[`examples/AGENTS.md`](examples/AGENTS.md) and the scoped
[plugin development rule](.cursor/rules/vestigo-plugin-development.mdc).
The example instructions are portable to independent plugin repositories;
[`docs/plugins.md`](docs/plugins.md) is the host API and deployment reference.

## Windows native interop

Prefer CsWin32-generated APIs, COM interfaces, structs, enums and constants from
`server/NativeMethods.txt`. Before adding handwritten interop, test the pinned
generator's support. Document any actual metadata/generation gap beside the
fallback; preserve native ownership, bounds, errors and DLL search restrictions.

## New MCP tools and modules

Update tests and every applicable documentation surface, not only catalogs:
`docs/tools.md`, `docs/security.md`, `docs/troubleshooting.md`,
`docs/configuration.md`, `docs/architecture.md`, `docs/windows-service.md`,
`README.md`, the `site/` GitHub Pages booklet, and `server/Hosting/LandingPage.html`. Keep installer module
metadata and dependencies aligned. Read `mcp-docs.mdc` for the full checklist.
Regenerate catalogs with `VESTIGO_REGENERATE_DOCS=1`, then verify tests again
without that variable. Report failures and environment-dependent skips honestly.
