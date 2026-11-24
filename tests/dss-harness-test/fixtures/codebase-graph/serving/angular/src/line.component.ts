import { Component, Input } from '@angular/core';
import { Line } from './lines';

/** One line of a cart, as a list item. */
@Component({
  selector: 'app-line',
  templateUrl: './line.component.html',
})
export class LineComponent {
  /** The line shown. */
  @Input() line: Line;

  /** The line as a receipt writes it. */
  lineText(): string {
    return this.line.name + ' x' + this.line.count;
  }

  /** Whether the line is one of something. */
  isSingle(): boolean {
    return this.line.count === 1;
  }
}
