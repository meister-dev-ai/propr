# How to contribute

Great you are here! We welcome contributions from the community to help us build and improve ProPR. Here are some ways you can get involved:

- **Report issues**: If you find a bug or have a feature request, please open an issue on GitHub. We use issues to track bugs, plan features, and organize our work.
- **Submit pull requests**: If you want to contribute code, please fork the repository and submit a pull request. We welcome contributions of all sizes, from small bug fixes to large features. Please make sure to follow our coding standards and include tests for your changes.

### Developer Certificate of Origin (DCO)

To keep the IP (Intellectual Property) clean for both our community and commercial users, we use a DCO process. By adding a `Signed-off-by` line to your commit messages, you certify that:

1. You created the contribution or have the right to submit it under the project's license (ELv2).
2. You grant **Andreas Rain** a non-exclusive, perpetual, worldwide, sublicensable license to use, modify, and distribute your contribution, including the right to sell it under a commercial license.

**How to sign your work:**
Use the `-s` flag when committing:
`git commit -s -m "My informative commit message"`

# Submitting a pull request

1. Fork the repository and create a new branch for your changes.
2. Make your changes and commit them with clear and descriptive messages.
3. Make sure to use the style and conventions used in the existing codebase. We recommend running `dotnet format` on the solution to ensure consistent formatting based on the checked-in `.editorconfig` rules.
4. Push your changes to your fork and submit a pull request to the main repository.

Note: We recommend using the pre-commit hook we provide via `scripts/setup-hooks.[ps1|sh]`.

# Size of pull requests

We encourage small, focused pull requests that are easier to review and merge. If you have a large change, consider breaking it down into smaller pieces or discussing it with the maintainers before submitting.

# AI contributions

We also welcome contributions that involve AI-generated content, such as documentation, code comments, or even code itself. Please state if you have used AI to generate any part of your contribution. We ask that you review and verify any AI-generated content for accuracy and quality before submitting, as we want to maintain a high standard for our codebase and documentation.

Additionally we encourage contributors of AI-generated content to verify that the contribution does not contain any sensitive or private information, and that it complies with our code of conduct and contribution guidelines, as well as any applicable laws and regulations.

Thanks,
the Meister DEV team
