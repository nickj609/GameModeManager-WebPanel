# Contributing to the GameModeManager Panel

We welcome and appreciate contributions of all kinds to the GameModeManager Panel! By participating in this project, you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md). Please take a moment to review it.

This document outlines the guidelines for contributing to this project. Following these guidelines helps ensure a smooth and collaborative experience for everyone involved.

## How Can I Contribute?

There are many ways you can contribute to the panel, including:

* **Reporting Bugs:** Help us identify and fix issues by reporting bugs in detail.
* **Suggesting Enhancements:** Share your ideas for new features, improvements, and optimizations.
* **Writing Code:** Contribute by implementing new features, fixing bugs, or refactoring existing code.
* **Improving Documentation:** Help us make our documentation clearer, more comprehensive, and easier to understand.
* **Creating Examples:** Develop examples that demonstrate how to use the panel.
* **Testing:** Help us ensure the stability and reliability of the project by testing new features and bug fixes.
* **Reviewing Pull Requests:** Provide valuable feedback on contributions from others.
* **Community Support:** Help answer questions and provide support to other users in our community forums or chat channels.

## Reporting Bugs

When reporting a bug, please include as much detail as possible. A good bug report includes:

* **A clear and concise title** that summarizes the issue.
* **Steps to reproduce the bug** in detail.
* **Expected behavior** - what did you expect to happen?
* **Actual behavior** - what actually happened?
* **Environment details** (e.g., operating system, browser version, software version).
* **Any relevant error messages or logs.**
* **Screenshots or screen recordings** if applicable.

Please check the [issue tracker](https://github.com/nickj609/GameModeManager-WebPanel/issues) to see if the bug has already been reported before creating a new one.

## Suggesting Enhancements

If you have an idea for a new feature or improvement, please:

* **Check the [issue tracker](https://github.com/nickj609/GameModeManager-WebPanel/issues)** to see if a similar suggestion already exists.
* **Open a new issue** with a clear and descriptive title.
* **Explain the problem** that your suggestion solves or the need it addresses.
* **Describe the proposed solution** in as much detail as possible.
* **Provide any relevant context or use cases.**
* **Indicate if you are willing to help implement the suggestion.**

## Contributing Code

If you'd like to contribute code to the panel, please follow these steps:

1. **Fork the repository** on GitHub.
2. **Create a new branch** for your contribution. Choose a descriptive name for your branch (e.g., `feature/new-feature`, `bugfix/issue-123`).
3. **Make your changes** following the project's coding style and best practices.
4. **Write tests** for any new functionality or bug fixes. Ensure that all tests pass.
5. **Update the documentation** if your changes introduce new features or modify existing ones.
6. **Commit your changes** with clear and concise commit messages. Follow conventional commit message guidelines if applicable.
7. **Push your branch** to your forked repository.
8. **Open a Pull Request (PR)** on GitHub against the `main` branch (or the designated development branch) of the main repository.

When submitting a pull request, please:

* **Provide a clear and concise title** for your PR.
* **Describe the changes** you've made and the purpose of your contribution.
* **Reference any related issues** (e.g., `Fixes #123`, `Implements #456`).
* **Indicate if your changes require any special attention or review.**

## Code Style

Please follow the style used throughout the project:

* **Pages are Razor Pages with htmx.** No Node, no build step for the browser side; scripts go in `wwwroot/js/panel.js`.
* **The Content Security Policy is strict:** no inline `<script>`, `<style>` or `style=` attributes. Colours are CSS variables in `wwwroot/css/site.css`.
* **Every word people see is translated.** Add new strings to all 13 `lang/*.json` files with `tools/LangTool` (`dotnet run --project tools/LangTool -- missing`, then `add rows.json`).
* **Keep labels short and plain**, and match the wording of GameModeManager's in-game menus.
* **A protocol change touches both sides:** the panel, GameModeManager's agent, `docs/samples/status.json` and [the agent protocol](docs/wiki/Agent-Protocol.md).

## Testing

The project has no unit tests yet. Before opening a pull request, please:

* **Build in Release with no new warnings:** `dotnet build GmmPanel.slnx -c Release`.
* **Check the translations:** `dotnet run --project tools/LangTool -- check` reports 0 problems.
* **Try your change in the browser** with stand-in game servers: `bash tools/demo.sh` starts the panel's `FakeAgent` for six sample servers.
* **For changes to GameModeManager's agent**, try them on a real server too.

## Documentation

If your contribution introduces new features or changes existing ones, please update the relevant documentation to reflect these changes. This includes README files, API documentation, tutorials, and any other project documentation.

## Pull Request Review Process

Once you submit a pull request, it will be reviewed by one or more maintainers. They may provide feedback or request changes. Please be responsive to their feedback and make the necessary adjustments. Once the review is complete and the changes are approved, your pull request will be merged into the main branch.

## Getting Help

If you have any questions or need help with contributing, please don't hesitate to reach out to the project maintainers or other contributors through Steam or Discord.

Thank you for your contributions! We appreciate your time and effort in making the GameModeManager Panel better.
