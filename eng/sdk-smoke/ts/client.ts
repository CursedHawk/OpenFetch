import createClient from "openapi-fetch";
import type { paths, components } from "./schema.js";
const client = createClient<paths>({ baseUrl: "http://localhost" });
const query: components["schemas"]["OrdersQuery"] = {
  select: ["id", "customer.name"],
  where: { type: "logical", operator: "and", children: [{ type: "comparison", field: "status", operator: "in", value: ["open"] }] },
  orderBy: [{ field: "createdAt", direction: "desc" }],
  page: { first: 10 },
};
export async function run() {
  const { data } = await client.POST("/query/orders", { body: query });
  const row = data?.data[0];
  const name: string | undefined = row?.customer?.name;
  return name;
}
