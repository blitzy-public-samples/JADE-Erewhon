// The locale-5129 message catalogue of Erewhon.Application (Messages/messages.en-NZ.json), read through the
// '@messages' alias, and the function that fills its {n} placeholders.

import messages from '@messages';

/** Key of a message in the catalogue. */
export type MessageKey = keyof typeof messages;

/** Every message template in the catalogue, keyed by message key. */
const catalogue: Readonly<Record<MessageKey, string>> = messages;

export { catalogue as messages };

const PLACEHOLDER = /\{(\d+)\}/g;

/**
 * Returns the template for `key` with each `{n}` replaced by `String(args[n])`; a placeholder without an argument at
 * its index is left unchanged, and arguments without a placeholder are ignored.
 */
export function msg(key: MessageKey, ...args: Array<string | number>): string {
  return catalogue[key].replace(PLACEHOLDER, (placeholder: string, index: string) => {
    const position = Number(index);
    return position < args.length ? String(args[position]) : placeholder;
  });
}
