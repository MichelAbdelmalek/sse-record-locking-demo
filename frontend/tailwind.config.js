/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ['./src/**/*.{html,ts}'],
  theme: {
    extend: {
      colors: {
        // Same names the templates use: slate neutrals, sky accent (matches the PrimeNG theme).
        surface: { 0: '#ffffff', ...require('tailwindcss/colors').slate },
        primary: require('tailwindcss/colors').sky,
      },
    },
  },
  plugins: [],
};
