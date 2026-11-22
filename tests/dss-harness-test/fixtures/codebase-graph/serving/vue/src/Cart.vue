<script setup lang="ts">
import LineItem from './LineItem.vue';
import type { Line } from './lines';

/** The lines bought. */
let lines: Line[] = [];

/** How many lines a cart holds at most. */
const MAX_LINES = 50;

/** Adds a line where the cart has room for one. */
function add(line: Line) {
  if (lines.length < MAX_LINES) {
    lines[lines.length] = line;
  }
}

/** The cart's total in cents. */
function total(): number {
  let total = 0;
  for (const line of lines) {
    total += line.cents * line.count;
  }
  return total;
}

/** How many lines the cart holds. */
function count(): number {
  return lines.length;
}

/** The line that costs the most, or the first where all cost alike. */
function dearest(): Line | undefined {
  let best: Line | undefined;
  for (const line of lines) {
    if (best === undefined || line.cents * line.count > best.cents * best.count) {
      best = line;
    }
  }
  return best;
}

/** Empties the cart. */
function clear(): void {
  lines = [];
}

/** Whether the cart has room for one more line. */
function hasRoom(): boolean {
  return lines.length < MAX_LINES;
}
</script>

<template>
  <main class="cart">
    <h1 class="title">Your cart</h1>
    <LineItem v-for="line in lines" :key="line.name" :line="line" />
    <button class="btn btn-primary" @click="add({ name: 'tea', count: 1, cents: 350 })">Add tea</button>
    <p>Total: {{ total() }}</p>
  </main>
</template>

<style scoped>
.title {
  font-size: 2rem;
}

.cart {
  max-width: 40rem;
  margin: 0 auto;
}
</style>
