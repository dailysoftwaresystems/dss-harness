import { Cart } from '../src/cart';
import { receipt } from '../src/receipt';

describe('receipt', () => {
  it('prints the total in money', () => {
    const cart = new Cart();
    cart.add({ name: 'tea', count: 2, cents: 350 });
    expect(receipt(cart)).toContain('Total 7.00');
  });
});
