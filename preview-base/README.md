# Rosenvall Ticket Preview Base

Prewarmed runtime image for AI-ticket previews.

The app source is generated per ticket and mounted into `/workspace`. This image carries the shared Vite, React, Tailwind, and shadcn-style dependencies under `/opt/rosenvall-preview/node_modules`, so preview pods do not need to run `npm install` during startup.
