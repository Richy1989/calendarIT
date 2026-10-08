// New categories cycle through these starter colors (exact CSS3-named hexes, so they
// round-trip losslessly through the iCalendar COLOR property).
export const CATEGORY_COLORS = ['#7B68EE', '#6495ED', '#40E0D0', '#3CB371', '#DAA520', '#DB7093', '#FF6347', '#708090']

/** The starter color for the next category, given how many exist. */
export const nextCategoryColor = (existing: number) => CATEGORY_COLORS[existing % CATEGORY_COLORS.length]
