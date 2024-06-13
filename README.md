# PgDump
Простой докер контейнер, позволяющий осуществлять дампы базы данных с определенной переодичностью в хранилище s3.

## Конфигурация
* `ConnectionStrings__Postgres` - строка подключения к бд
* `S3StorageOptions__ServiceUrl` - урл сервиса s3
* `S3StorageOptions__AccessKeyId` - Id сервисного аакаунта
* `S3StorageOptions__SecretAccessKey` - секретный ключ доступа
* `S3StorageOptions__BucketName` - название бакета
* `S3StorageOptions__PathPrefix` - префик пути
* `ScheduleCron` - cron выражение, задающее расписание создания дампов
* `PgDumpOptions__ExtraArgs` - дополнительные аргументы командной строки для pg_dump

## Работа
Дампы базы сохраняются в хранилище s3 по ключу `<префикс пути>/dump_<дата и время дампа>.backup`. Также в этой же папке находится файл с название `latest.backup`, в который перезаписывается актуальное состояние базы. 

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
      PgDumpOptions__ExtraArgs: "-Fc" # переключает pg_dump в режим custom, что уменьшает объем дампа, но увеличивает время его сниятия
      ScheduleCron: "0 0/5 * * * ?"
  db:
    image: 'postgres:16.2'
```
