import { Cart } from './cart';
import { lineText, money, pad, place, rule } from './format';

/** A cart's receipt: each line, then the total in money. */
export function receipt(cart: Cart): string {
  let text = '';
  for (const line of cart.lines) {
    text += lineText(line) + '\n';
  }
  return text + 'Total ' + money(cart.total());
}

/** A cart's receipt with its lines numbered and its total in a column. */
export function columnReceipt(cart: Cart, width: number): string {
  let text = '';
  for (const line of cart.lines) {
    text += pad(place(cart.lines.indexOf(line) + 1) + ' ' + lineText(line), width) + money(line.cents) + '\n';
  }
  return text + rule(width) + '\n' + pad('Total', width) + money(cart.total());
}

/** A cart's receipt as a Markdown bullet list, its total bold. */
export function markdownReceipt(cart: Cart): string {
  let text = '';
  for (const line of cart.lines) {
    text += '- ' + lineText(line) + '\n';
  }
  return text + '**Total ' + money(cart.total()) + '**\n';
}

/** How many lines a receipt has room for: a cart's most, less one for its total. */
export function receiptLines(): number {
  return 49;
}
