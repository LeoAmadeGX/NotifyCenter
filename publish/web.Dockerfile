FROM node:22-alpine AS build
WORKDIR /app

COPY src/NotifyCenter.Web/package*.json ./src/NotifyCenter.Web/
WORKDIR /app/src/NotifyCenter.Web
RUN npm install

COPY src/NotifyCenter.Web ./
RUN npm run build

FROM nginx:1.27-alpine AS final
EXPOSE 17051

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/src/NotifyCenter.Web/dist /usr/share/nginx/html
