/** A line of a cart: something bought, so many of it, at a price in cents. */
export interface Line {
  name: string;
  count: number;
  cents: number;
}
