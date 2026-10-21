/**
 * What counts as a phone: narrower than the desktop layout, or a touch screen too short for it (a phone held
 * sideways). Phones get the classic page instead of the demo PC; classic.css hides the demo link with the same query.
 */
export const phoneQuery = '(max-width: 899px), (pointer: coarse) and (max-height: 500px)';
