/** Marks a button as the page's primary action, as the style sheet's .btn-primary styles it. */
export function markPrimary(button: HTMLElement): void {
  button.classList.add('btn-primary');
}

/** The page's primary button, found by the selector the style sheet writes. */
export function primaryButton(): Element | null {
  return document.querySelector('button.btn-primary');
}
