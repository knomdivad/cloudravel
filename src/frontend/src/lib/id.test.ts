import { describe, expect, it, vi } from 'vitest';
import { newId } from './id';

const uuidV4 =
  /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

describe('newId', () => {
  it('returns a UUID v4 when crypto.randomUUID is undefined', () => {
    vi.stubGlobal('crypto', {
      getRandomValues: (arr: Uint8Array) => {
        for (let i = 0; i < arr.length; i += 1) {
          arr[i] = (i * 17 + 3) % 256;
        }
        return arr;
      },
    });

    expect(newId()).toMatch(uuidV4);
  });
});
