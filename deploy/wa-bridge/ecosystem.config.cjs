// pm2: one process, restarted if it dies or grows past 700 MB; the secret comes from /opt/wa-bridge/.env (ENV_FILE).
module.exports = {
  apps: [{
    name: 'wa-bridge',
    script: 'src/main.mjs',
    cwd: __dirname,
    node_args: '--max-old-space-size=512',
    env: { NODE_ENV: 'production', ENV_FILE: '/opt/wa-bridge/.env' },
    max_memory_restart: '700M',
    kill_timeout: 8000,            // time to close every connection cleanly (the logins stay)
    exp_backoff_restart_delay: 2000,
    time: true,
  }],
};
