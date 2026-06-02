FROM node:22-alpine AS build
ARG VITE_BASE_PATH=/
WORKDIR /app

COPY src/NotifyCenter.Web/package*.json ./src/NotifyCenter.Web/
WORKDIR /app/src/NotifyCenter.Web
RUN npm install

COPY src/NotifyCenter.Web ./
RUN VITE_BASE_PATH="${VITE_BASE_PATH}" npm run build && \
    find /app/src/NotifyCenter.Web/dist -type f -exec chmod 644 {} +

FROM nginx:1.27-alpine AS final
ARG NGINX_BASE_PATH=/
EXPOSE 17051

COPY nginx.conf /tmp/nginx.conf.template
RUN BASE_PATH="${NGINX_BASE_PATH}" envsubst '${BASE_PATH}' < /tmp/nginx.conf.template > /etc/nginx/conf.d/default.conf
COPY --from=build /app/src/NotifyCenter.Web/dist /usr/share/nginx/html
