import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { resolve } from 'path';

// Multi-entry library build emitting ESM modules into dist/, deployed
// by `just install` to GameData/Nova/UI/. One entry per scene Nova
// owns; Dragonglass's hud shell dynamic-imports the matching module
// when SceneMapTopic routes the active scene (see
// NovaUiOverrideAddon.cs for the C# registration, and
// dragonglass@f1339cc1 for the scene-routed shell that replaced the
// old single-entry OverrideEntry path). Filenames stay stable across
// builds — DG's sidecar boots once and hashed names would force a
// restart on every UI rebuild.
//
// Externals: every shared-runtime specifier (svelte, three, threlte,
// every `@dragonglass/*` JS export) is treated as runtime-resolved by
// Dragonglass's importmap, so Nova's bundle never bakes in a duplicate
// — one Svelte runtime, shared with the hud shell and every other UI
// mod. Function-form `external` rather than the array-form regex so
// CSS imports can be exempted: `@dragonglass/instruments/flight.css`
// must be BUNDLED (the flight-layout classes left runtime.css when DG
// went pure-infrastructure), but its JS sibling `@dragonglass/instruments`
// must stay external. Stripping `?inline`/`?raw` suffixes before the
// match keeps Vite-augmented CSS specifiers covered.
function isExternal(source: string): boolean {
  const base = source.split('?')[0];
  if (base.endsWith('.css')) return false;
  if (source === 'svelte' || source === 'three' || source === '@threlte/core') return true;
  if (/^svelte\//.test(source)) return true;
  if (/^three\//.test(source)) return true;
  if (/^@dragonglass\//.test(source)) return true;
  return false;
}

export default defineConfig({
  // emitCss=false: Svelte component styles inject via runtime <style>
  // tags. Plain `.css` imports (e.g. flight.ts's flight.css adoption)
  // still go through Vite's CSS pipeline — they become CSSStyleSheet
  // objects via the `with { type: 'css' }` import attribute and the
  // mod adopts them into `document.adoptedStyleSheets` directly.
  plugins: [svelte({ emitCss: false })],
  build: {
    lib: {
      entry: {
        mainmenu: resolve(__dirname, 'src/mainmenu.ts'),
        flight:   resolve(__dirname, 'src/flight.ts'),
        editor:   resolve(__dirname, 'src/editor.ts'),
        rnd:      resolve(__dirname, 'src/rnd.ts'),
      },
      formats: ['es'],
    },
    rollupOptions: {
      external: isExternal,
      output: {
        entryFileNames: '[name].js',
        chunkFileNames: 'chunks/[name]-[hash].js',
        assetFileNames: '[name][extname]',
      },
    },
    minify: false,
    modulePreload: false,
    outDir: 'dist',
    emptyOutDir: true,
  },
});
