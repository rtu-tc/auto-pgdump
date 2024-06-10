# PgDump
Простой докер контейнер, позволяющий осуществлять дампы базы данных с определенной переодичностью в хранилище s3.

## Конфигурация
* `ConnectionStrings__Postgres` - строка подключения к бд
* `S3StorageOptions__ServiceUrl` - урл сервиса s3
* `S3StorageOptions__AccessKeyId` - Id сервисного аакаунта
* `S3StorageOptions__SecretAccessKey` - секретный ключ доступа
* `S3StorageOptions__BucketName` - название бакета 
* `ScheduleCron` - cron выражение, задающее расписание создания дампов

## Работа
Дампы базы сохраняются в хранилище s3 по ключу `pg_dump/<название базы>/dump_<дата и время дампа>.sql`. Также в этой же папке находится файл с название `latest.sql`, в который перезаписывается актуальное состояние базы. 

## Пример использования
Данный контейнер можно добавить как сервис в docker-compose.yml
``` yml
services:
  pgdump:
    image: 'ghcr.io/rtu-tc/auto-pgdump:16-2'
    environment:
      ConnectionStrings__Postgres: "Host=db;Port=5432;Database=<название базы данных>;Username=<имя пользователя>;Password=<пароль>"
      S3StorageOptions__ServiceUrl: "<адрес s3 хранилища>"
      S3StorageOptions__AccessKeyId: "<ид ключа>"
      S3StorageOptions__SecretAccessKey: "<ключ>"
      S3StorageOptions__BucketName: "<имя бакета>"
      S3StorageOptions__ForcePathStyle: false
      ScheduleCron: "0 0/5 * * * ?"
  db:
    image: 'postgres:16.2'
```
