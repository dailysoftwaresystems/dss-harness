/** A line of a cart: something bought, so many of it, at a price in cents. */
export interface Line {
  name: string;
  count: number;
  cents: number;
}

/** The most lines a cart holds. */
export const MAX_LINES = 50;

/** A cart of lines, totalled and counted. */
export class Cart {
  lines: Line[] = [];

  /** Adds a line to the cart where it has room for one. */
  add(line: Line): void {
    if (this.lines.length < MAX_LINES) {
      this.lines[this.lines.length] = line;
    }
  }

  /** The cart's total in cents. */
  total(): number {
    let total = 0;
    for (const line of this.lines) {
      total += line.count * line.cents;
    }
    return total;
  }

  /** How many lines the cart holds. */
  count(): number {
    return this.lines.length;
  }

  /** The line at a place of the cart, or nothing where the place is past its lines. */
  lineAt(place: number): Line | undefined {
    return place < this.lines.length ? this.lines[place] : undefined;
  }

  /** Whether the cart holds no line. */
  isEmpty(): boolean {
    return this.lines.length === 0;
  }

  /** The line that costs the most, or nothing where the cart holds no line. */
  dearest(): Line | undefined {
    let best: Line | undefined;
    for (const line of this.lines) {
      if (best === undefined || line.count * line.cents > best.count * best.cents) {
        best = line;
      }
    }
    return best;
  }

  /** Takes a line out of the cart where it is there. */
  remove(line: Line): boolean {
    const place = this.lines.indexOf(line);
    if (place < 0) {
      return false;
    }
    this.lines.splice(place, 1);
    return true;
  }

  /** Empties the cart. */
  clear(): void {
    this.lines.length = 0;
  }

  /** How many items the cart holds, every line's count together. */
  items(): number {
    let items = 0;
    for (const line of this.lines) {
      items += line.count;
    }
    return items;
  }
}
