from django.db import models


class UserModel(models.Model):
    name = models.CharField(max_length=100)
    age = models.IntegerField(default=0)
    code = db.Column(db.String)
    label = Field(default="x")
