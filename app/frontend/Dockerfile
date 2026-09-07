FROM docker.io/library/node:24-alpine AS build
WORKDIR /src

COPY package*.json ./
RUN npm ci

COPY . .
RUN npx ng build --configuration production

FROM docker.io/library/nginx:alpine AS final
COPY --from=build /src/dist/frontend/browser /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 8080
