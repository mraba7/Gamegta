// SPDX-License-Identifier: AGPL-3.0-only
// Text direction of a subtitle, decided from the text itself: the PC sends plain text, so the TV needs no new
// message field. ES5, like the rest of the TV app; loaded before app.js (and by the node test in tv/tests).
(function (root) {
  'use strict';

  // Hebrew, Arabic, Syriac, Thaana, NKo, Samaritan, Mandaic, Arabic Extended-A (U+0590-U+08FF) and the Hebrew and
  // Arabic presentation forms (U+FB1D-U+FEFC). Arabic-Indic digits are in the first range.
  var RTL = /[֐-ࣿיִ-ﻼ]/;

  // 'rtl' when the text has any right-to-left letter or digit, wherever it stands: a line that opens with a Latin
  // speaker label ("JASON: ...") is still an Arabic line. dir="auto" would get that case wrong.
  function directionOf(text) {
    return typeof text === 'string' && RTL.test(text) ? 'rtl' : 'ltr';
  }

  var api = { directionOf: directionOf };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.GAMETRANSLATOR_DIRECTION = api;
})(typeof window !== 'undefined' ? window : this);
