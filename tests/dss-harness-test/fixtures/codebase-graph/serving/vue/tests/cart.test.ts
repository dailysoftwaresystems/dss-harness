import { mount } from '@vue/test-utils';
import Cart from '../src/Cart.vue';

describe('Cart', () => {
  it('adds a line from its button', () => {
    const wrapper = mount(Cart);
    wrapper.find('button').trigger('click');
  });
});
