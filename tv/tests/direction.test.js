// SPDX-License-Identifier: AGPL-3.0-only
// Run: node tv/tests/direction.test.js
'use strict';
var assert = require('assert');
var direction = require('../webos/direction.js');

var cases = [
  ['JASON: مرحبا', 'rtl'],          // Latin speaker label first, Arabic sentence after it
  ['مرحبا يا صديقي', 'rtl'],
  ['Hello', 'ltr'],
  ['', 'ltr'],
  ['١٢٣ go', 'rtl'],                // Arabic-Indic digits
  ['Merhaba, nasılsın?', 'ltr'],    // Turkish keeps left-to-right
  ['שלום', 'rtl'],
  ['...', 'ltr']
];
cases.forEach(function (c) {
  assert.strictEqual(direction.directionOf(c[0]), c[1], JSON.stringify(c[0]));
});
assert.strictEqual(direction.directionOf(null), 'ltr');
assert.strictEqual(direction.directionOf(undefined), 'ltr');
console.log('direction: ' + (cases.length + 2) + ' checks passed');
