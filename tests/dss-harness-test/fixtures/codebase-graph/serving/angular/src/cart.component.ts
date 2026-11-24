import { Component } from '@angular/core';
import { Line } from './lines';

/** A cart of lines, totalled. */
@Component({
  selector: 'app-cart',
  templateUrl: './cart.component.html',
  styleUrls: ['./cart.component.css'],
})
export class CartComponent {
  /** The lines bought. */
  lines: Line[] = [];

  /** How many lines a cart holds at most. */
  static readonly MAX_LINES = 50;

  /** Adds a line where the cart has room for one. */
  add(line: Line): void {
    if (this.lines.length < CartComponent.MAX_LINES) {
      this.lines[this.lines.length] = line;
    }
  }

  /** The cart's total in cents. */
  total(): number {
    let total = 0;
    for (const line of this.lines) {
      total += line.cents * line.count;
    }
    return total;
  }

  /** How many lines the cart holds. */
  count(): number {
    return this.lines.length;
  }

  /** The line that costs the most, or the first where all cost alike. */
  dearest(): Line | undefined {
    let best: Line | undefined;
    for (const line of this.lines) {
      if (best === undefined || line.cents * line.count > best.cents * best.count) {
        best = line;
      }
    }
    return best;
  }

  /** Empties the cart. */
  clear(): void {
    this.lines = [];
  }

  /** Whether the cart has room for one more line. */
  hasRoom(): boolean {
    return this.lines.length < CartComponent.MAX_LINES;
  }

  /** Marks an element as the primary action, as the style sheet's .btn-primary styles it. */
  markPrimary(element: HTMLElement): void {
    element.classList.add('btn-primary');
  }

  /** The primary action, found by the selector the style sheet writes. */
  primaryAction(): Element | null {
    return document.querySelector('button.btn-primary');
  }
}
