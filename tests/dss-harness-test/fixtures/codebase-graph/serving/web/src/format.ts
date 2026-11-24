/** A number of cents as money is written: 1234 is 12.34. */
export function money(cents: number): string {
  const whole = (cents - (cents % 100)) / 100;
  const part = cents % 100;
  return whole + '.' + (part < 10 ? '0' : '') + part;
}

/** One line of a cart as a receipt writes it. */
export function lineText(line: { name: string; count: number }): string {
  return line.name + ' x' + line.count;
}

/** A name as a receipt's heading writes it: each word's first letter in upper case, the rest as it is. */
export function titleCase(text: string): string {
  let titled = '';
  let start = true;
  for (const letter of text) {
    titled += start ? letter.toUpperCase() : letter;
    start = letter === ' ';
  }
  return titled;
}

/** A text in a field of a width, padded with spaces on its right. */
export function pad(text: string, width: number): string {
  let padded = text;
  while (padded.length < width) {
    padded += ' ';
  }
  return padded;
}

/** The heading every receipt begins with. */
export const RECEIPT_HEADING = 'Your receipt';

/** A number as a receipt writes a place in it: 1 is 1., 12 is 12. */
export function place(number: number): string {
  return number + '.';
}

/** A line of a receipt repeated, as a receipt's rule is written. */
export function rule(width: number): string {
  let line = '';
  for (let at = 0; at < width; at++) {
    line += '-';
  }
  return line;
}

/** Whether a name is one the shop sells: it says something and costs nothing to spell. */
export function isName(name: string): boolean {
  return name.trim().length > 0;
}
