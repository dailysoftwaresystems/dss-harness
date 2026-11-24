<script>
/** One line of a cart, as a list item: the line it is given, written as a receipt writes it. */
export default {
  props: { line: Object },
  computed: {
    lineText() {
      return this.line.name + ' x' + this.line.count;
    },
  },
};
</script>

<template>
  <li class="line">{{ lineText }}</li>
</template>

<style scoped>
.line {
  padding: 0.25rem 0;
}
</style>
