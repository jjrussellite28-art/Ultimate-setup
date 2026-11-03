// Minimal lint for the plain-JS UI and the check scripts. Only rules that catch real bugs, no style rules.
// Run from the repository root: npm ci --prefix scripts && scripts/node_modules/.bin/eslint
import globals from './scripts/node_modules/globals/index.js';

const rules = {
  'no-useless-escape': 'error',
  'no-unused-vars': ['error', { args: 'none', caughtErrors: 'none' }],
  'no-undef': 'error',
  'no-dupe-keys': 'error',
  'no-empty': ['error', { allowEmptyCatch: true }],
  eqeqeq: ['error', 'smart'],
};

export default [
  { ignores: ['**/node_modules/**', 'ui/assets/**', 'ui/skins/**/*.min.js'] },
  {
    files: ['ui/**/*.js'],
    languageOptions: { ecmaVersion: 'latest', sourceType: 'module', globals: { ...globals.browser } },
    rules,
  },
  {
    files: ['scripts/*.mjs'],
    languageOptions: { ecmaVersion: 'latest', sourceType: 'module', globals: { ...globals.node } },
    rules,
  },
  {
    files: ['scripts/*.js'],
    languageOptions: { ecmaVersion: 'latest', sourceType: 'commonjs', globals: { ...globals.node } },
    rules,
  },
];
