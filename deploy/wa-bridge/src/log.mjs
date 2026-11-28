// One line per event: time, level, what, and ids only. Never message text or a customer's number.
const LEVELS = { debug: 10, info: 20, warn: 30, error: 40 };

export function makeLog(level = 'info', out = console) {
  const min = LEVELS[level] ?? 20;
  const line = (lv, msg, data) => {
    if (LEVELS[lv] < min) return;
    const extra = data ? ' ' + JSON.stringify(data) : '';
    (lv === 'error' || lv === 'warn' ? out.error : out.log).call(out, `${new Date().toISOString()} ${lv.toUpperCase()} ${msg}${extra}`);
  };
  return { debug: (m, d) => line('debug', m, d), info: (m, d) => line('info', m, d), warn: (m, d) => line('warn', m, d), error: (m, d) => line('error', m, d) };
}
