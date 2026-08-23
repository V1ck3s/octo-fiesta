FROM node:22-alpine

WORKDIR /app

COPY package.json ./
RUN npm install --omit=dev

COPY server.js ./
COPY api ./api

EXPOSE 3000

CMD ["node", "server.js"]
