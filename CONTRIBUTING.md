# Contributing to Copaste

Thanks for your interest. Copaste is a small project maintained by one person, so a few simple rules keep things manageable.

## Reporting a bug

Open a [bug report](https://github.com/viktorsx/Copaste/issues/new?template=bug_report.yml). The form asks for the steps, versions and the `Copaste.log` file, and it shows where the log lives. Reports with a log get fixed much faster than reports without one. Every report is read.

## Suggesting a feature

Post it in [Ideas](https://github.com/viktorsx/Copaste/discussions/categories/ideas) to talk it through, or open a [feature request](https://github.com/viktorsx/Copaste/issues/new?template=feature_request.yml) if it is already well defined. Describe the situation in your city where the current tool falls short; that is more useful than a solution.

A few things are out of scope on purpose: anything that writes custom data into save files, and copying road networks (that is Move It territory).

## Sending code

Pull requests are welcome for bug fixes and small, focused improvements. Before a bigger change, open an issue or discussion first so we agree on the approach.

- Keep the change focused: one fix or one feature per pull request
- Test it in the game and say what you tested in the description
- Do not add dependencies; the mod deliberately has no Harmony patches and no custom save data, and that stays
- Match the existing style; comments in the tool code are in Serbian, documentation is in English
- Update the docs (`docs/`) and `CHANGELOG.md` when behavior changes
- Localization strings live in `src/Localization.cs` in four languages (EN, DE, FR, SR); new options need all four

Getting started: [docs/README.md](docs/README.md) has the reading order, [docs/build-and-deploy.md](docs/build-and-deploy.md) explains the build. Each developer document ends with a Gotchas section; reading them first saves time.

## Translations

Translations for other languages are welcome. Open an issue and I will point you to the strings.
