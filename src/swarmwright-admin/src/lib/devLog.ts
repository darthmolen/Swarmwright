// Tiny namespaced console logger for dev observability. Each namespace gets a
// consistent color-coded `[name]` prefix so you can scan or filter the browser
// console. Levels map to the native console methods (info/warn/error/debug) so
// browser devtools' level filter still works.
//
// Usage:
//   const log = devLog('api');
//   log.info('POST /foo -> 204', { ms: 123 });
//   log.warn('fallback path used');
//   log.error('fetch failed', err);
//   log.debug('payload', payload);

export interface DevLog {
  info: (...args: unknown[]) => void;
  warn: (...args: unknown[]) => void;
  error: (...args: unknown[]) => void;
  debug: (...args: unknown[]) => void;
}

// Dracula-ish palette — readable on both light and dark backgrounds.
const PALETTE = [
  '#8be9fd', // cyan
  '#50fa7b', // green
  '#ffb86c', // orange
  '#ff79c6', // pink
  '#bd93f9', // purple
  '#f1fa8c', // yellow
  '#ff5555', // red
  '#6272a4', // slate
];

const assigned = new Map<string, string>();

function colorFor(namespace: string): string {
  const cached = assigned.get(namespace);
  if (cached) return cached;
  const color = PALETTE[assigned.size % PALETTE.length];
  assigned.set(namespace, color);
  return color;
}

export function devLog(namespace: string): DevLog {
  const prefix = `%c[${namespace}]`;
  const style = `color:${colorFor(namespace)};font-weight:bold`;
  return {
    info: (...args) => console.info(prefix, style, ...args),
    warn: (...args) => console.warn(prefix, style, ...args),
    error: (...args) => console.error(prefix, style, ...args),
    debug: (...args) => console.debug(prefix, style, ...args),
  };
}
