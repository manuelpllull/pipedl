#!/bin/zsh
set -euo pipefail

echo "Listing Syncify paths before removal:"
find . -type d -name 'Syncify*' -print -o -name 'Syncify.sln' -print || true

# Remove leftover Syncify directories and solution
rm -rf src/Syncify.Domain src/Syncify.Infrastructure src/Syncify.Tests src/Syncify.Worker Syncify.sln

echo "Removal complete. Remaining Syncify matches (should be none):"
grep -R --line-number --ignore-case "Syncify" || true
