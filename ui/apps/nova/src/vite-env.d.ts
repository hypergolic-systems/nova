/// <reference types="vite/client" />

// Vite's `?inline` query returns a CSS file's contents as a string
// (used by flight.ts to build a CSSStyleSheet for adoptedStyleSheets).
// Vite ships the type alias for `?inline` on JS files but not CSS.
declare module '*.css?inline' {
  const content: string;
  export default content;
}
