FROM node:22-alpine

WORKDIR /opt/rosenvall-preview

ENV NODE_ENV=development \
    npm_config_update_notifier=false \
    npm_config_fund=false \
    npm_config_audit=false

COPY preview-base/package.json ./package.json
RUN npm install --no-audit --no-fund \
    && npm cache clean --force \
    && chown -R node:node /opt/rosenvall-preview

USER node
WORKDIR /workspace

EXPOSE 8080
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0", "--port", "8080"]
