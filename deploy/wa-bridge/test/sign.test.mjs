import test from 'node:test';
import assert from 'node:assert/strict';
import { sign, verify, safeEqual, secretFrom, mediaSig, mediaUrl, checkMedia } from '../src/sign.mjs';

const SECRET = 'test-secret-0123456789abcdef0123456789';

test('a fixed vector (Sellybot checks the same one in wa-unit.ps1)', () => {
  assert.equal(sign(SECRET, '{"event":"ping"}', 1764300000), 't=1764300000,v1=8e93dd0e2007918f384fda7608ff83efbb3a896dca2c289068a6e6fac1d307e3');
});

test('a signature checks out with the same secret, body and a fresh time', () => {
  const body = JSON.stringify({ event: 'message', instance_id: 'wa1', payload: { text: 'سلام' } });
  const h = sign(SECRET, body, 1000);
  assert.equal(verify(SECRET, h, body, 1000).ok, true);
  assert.equal(verify(SECRET, h, body, 1000 + 299).ok, true);
});

test('a wrong secret, a changed body, an old or missing signature are refused', () => {
  const body = '{"a":1}', h = sign(SECRET, body, 1000);
  assert.equal(verify(SECRET + 'x', h, body, 1000).why, 'bad_signature');
  assert.equal(verify(SECRET, h, '{"a":2}', 1000).why, 'bad_signature');
  assert.equal(verify(SECRET, h, body, 1000 + 301).why, 'too_old');
  assert.equal(verify(SECRET, h, body, 1000 - 301).why, 'too_old');
  assert.equal(verify(SECRET, '', body, 1000).why, 'no_signature');
  assert.equal(verify(SECRET, 't=abc,v1=00', body, 1000).why, 'no_signature');
  assert.equal(verify('', h, body, 1000).why, 'no_secret');
});

test('safeEqual compares whole strings only', () => {
  assert.equal(safeEqual('abc', 'abc'), true);
  assert.equal(safeEqual('abc', 'abd'), false);
  assert.equal(safeEqual('abc', 'abcd'), false);
  assert.equal(safeEqual('', ''), false);
  assert.equal(safeEqual(undefined, 'x'), false);
});

test('the API secret comes from X-Bridge-Secret or a Bearer token', () => {
  assert.equal(secretFrom({ 'x-bridge-secret': 's1' }), 's1');
  assert.equal(secretFrom({ authorization: 'Bearer s2' }), 's2');
  assert.equal(secretFrom({ authorization: 'Basic s2' }), '');
  assert.equal(secretFrom({}), '');
});

test('media links are signed, expire, and are bound to their file', () => {
  const url = mediaUrl('https://wa.example.com', SECRET, 'wa1', 'ABC.jpg', 3600, 1000);
  const u = new URL(url);
  assert.equal(u.pathname, '/media/wa1/ABC.jpg');
  const exp = u.searchParams.get('exp'), sig = u.searchParams.get('sig');
  assert.equal(exp, '4600');
  assert.equal(sig, mediaSig(SECRET, 'wa1', 'ABC.jpg', 4600));
  assert.equal(checkMedia(SECRET, 'wa1', 'ABC.jpg', exp, sig, 2000), true);
  assert.equal(checkMedia(SECRET, 'wa1', 'ABC.jpg', exp, sig, 4601), false);   // expired
  assert.equal(checkMedia(SECRET, 'wa1', 'OTHER.jpg', exp, sig, 2000), false);
  assert.equal(checkMedia(SECRET, 'wa2', 'ABC.jpg', exp, sig, 2000), false);
  assert.equal(checkMedia(SECRET, 'wa1', 'ABC.jpg', '9999', sig, 2000), false); // a longer life needs a new signature
});
