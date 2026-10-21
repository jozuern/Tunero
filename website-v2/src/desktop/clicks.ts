// Buttons that swap in place (Fix and Undo, Remove and the next row's Remove) must not take the second click of a
// double click, or a double click would undo what the first click did.
export const single =
  <E extends MouseEvent>(handler: (event: E) => void) =>
  (event: E) => {
    if (event.detail > 1) return;
    handler(event);
  };
